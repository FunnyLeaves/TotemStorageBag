using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Duckov.Economy;
using Duckov.ItemBuilders;
using Duckov.UI;
using Duckov.Utilities;
using HarmonyLib;
using ItemStatsSystem;
using SodaCraft.Localizations;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TotemStorageBag
{
    /// <summary>
    /// 游戏 Mod 入口。
    /// 游戏按 info.ini 的 name（TotemStorageBag）作为命名空间，加载名为 ModBehaviour 的类，
    /// 并要求继承 Duckov.Modding.ModBehaviour。
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        public const string ModName = "TotemStorageBag";
        // 游戏内道具名（项目/Mod 名仍为 TotemStorageBag）
        public const string BagDisplayName = "图腾卷轴";
        public const string BagDescription = "可收纳图腾的卷轴，需要吸收任意三个III级图腾制作。";
        public const float BagWeight = 1.0f;
        public const string BagIconFileName = "图腾卷轴.png";
        /// <summary>自定义图标显示比例：0.85 表示内容约占图标区域 85%（留边缩小，接近钥匙包视觉大小）。</summary>
        public const float BagIconScale = 0.85f;
        public const string RecipeId = "TotemStorageBag";
        public const int SlotCount = 8;

        /// <summary>图腾卷轴（收纳包）原始价值（商人售价 = 原始价值 × 0.5 = 9444）。</summary>
        public const int BagRawPrice = 18888;

        public const string TotemTagName = "Totem";
        public const string ContainerTagName = "Continer";
        public const string WorkbenchTagName = "WorkBenchAdvanced";
        public const string FeatherDisplayNameKey = "Item_Feather";
        public const int FeatherCount = 20;
        public const int CraftMoneyCost = 2000;
        public const string AnyTotemDisplayName = "图腾:任意Ⅲ级";
        /// <summary>占位图腾悬停第一行的渲染文本（本地化 key 未命中时游戏会显示 *key*，需覆盖去掉星号）。</summary>
        public const string AnyTotemDisplayText = "图腾：任意 III 级";
        public const string AnyTotemDescription = "蕴含庞大能量的图腾。";
        /// <summary>占位材料图标参考：抗空间 Ⅲ（橙色高级图腾图标）。</summary>
        public const int IconReferenceTypeID = 971;

        /// <summary>注册成功的动态物品 TypeID；未注册为 -1。</summary>
        public static int BagTypeID { get; private set; } = -1;

        private static Item? _bagPrefab;
        private static Item? _keyRingPrefab;
        private static int _featherTypeID = -1;
        private static int _placeholderTypeID = -1;
        private static readonly HashSet<int> _allowedTotemTypeIDs = new HashSet<int>();
        private static ModConfig _config = new ModConfig();
        private static bool _giveDone;
        private static bool _filtersLogged;
        private static bool _harmonyApplied;
        private static bool _hoverSubscribed;
        private static FieldInfo? _hoverDescriptionField;
        private static bool _showInvCountPatched;
        private static bool _showInvCountGaveUp;
        private static int _showInvCountRetry;
        private static PropertyInfo? _craftViewInstanceProp;
        private static PropertyInfo? CraftViewInstanceProp =>
            _craftViewInstanceProp ??= typeof(CraftView).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic);

        void Awake()
        {
            Debug.Log("[TotemStorageBag] Awake: ModBehaviour created.");
        }

        void Update()
        {
            // 事件可能因时序错过（如仓库在订阅前已加载完成），Update 兜底，只检查标志，开销可忽略
            if (!_giveDone && _config.GiveItemOnStart && BagTypeID >= 0
                && PlayerStorage.Instance != null && PlayerStorage.Inventory != null && !PlayerStorage.Loading)
            {
                TryGiveBagToPlayer();
            }

            // 工作台打开时打印一次过滤分区映射，用于校准“其他”分区
            if (!_filtersLogged)
            {
                object? craftView = CraftViewInstanceProp?.GetValue(null);
                if (craftView != null)
                {
                    _filtersLogged = true;
                    LogCraftViewFilters(craftView);
                }
            }

            // ShowInventoryCount 可能晚于本 Mod 加载，轮询到它的 Util 类型后再打补丁
            EnsureShowInvCountPatch();
        }

        protected override void OnAfterSetup()
        {
            try
            {
                _config = ModConfig.Load();
                ApplyLocalizationOverrides();
                RegisterTotemBag();
                RegisterTotemMaterialPlaceholder();
                ResolveAllowedTotems();
                ResolveFeatherTypeID();
                DumpTotemItems();
                RegisterCraftingRecipe();
                ApplyCraftPatches();
                EnsureHoverSubscribed();
                Debug.Log($"[TotemStorageBag] 图腾卷轴注册成功。TypeID={BagTypeID}, 槽位数={SlotCount}, 原始价值={_bagPrefab!.Value}");

                // 仓库加载完成后再发放，避免主菜单阶段玩家仓库不存在导致静默失败
                PlayerStorage.OnLoadingFinished += OnPlayerStorageLoaded;
                TryGiveBagToPlayer();
            }
            catch (Exception ex)
            {
                Debug.LogError("[TotemStorageBag] OnAfterSetup 失败: " + ex);
            }
        }

        void OnDestroy()
        {
            PlayerStorage.OnLoadingFinished -= OnPlayerStorageLoaded;
            if (_hoverSubscribed)
            {
                ItemHoveringUI.onSetupMeta -= OnHoverMeta;
                _hoverSubscribed = false;
            }
            if (_bagPrefab != null)
            {
                ItemAssetsCollection.RemoveDynamicEntry(_bagPrefab);
                _bagPrefab = null;
                BagTypeID = -1;
            }
        }

        private static void OnPlayerStorageLoaded()
        {
            try
            {
                RegisterCraftingRecipe();
                TryGiveBagToPlayer();
            }
            catch (Exception ex)
            {
                Debug.LogError("[TotemStorageBag] OnPlayerStorageLoaded 失败: " + ex);
            }
        }

        /// <summary>
        /// 本地化 key 未命中时，游戏会显示 *key*（如 *图腾卷轴*、*图腾:任意Ⅲ级*）。
        /// 用覆盖文本注册，使名称/描述按真实文案显示。
        /// </summary>
        private static void ApplyLocalizationOverrides()
        {
            try
            {
                LocalizationManager.SetOverrideText(BagDisplayName, BagDisplayName);
                LocalizationManager.SetOverrideText(BagDisplayName + "_Desc", BagDescription);
                LocalizationManager.SetOverrideText(AnyTotemDisplayName, AnyTotemDisplayText);
                LocalizationManager.SetOverrideText(AnyTotemDisplayName + "_Desc", AnyTotemDescription);
                Debug.Log("[TotemStorageBag] 本地化覆盖已注册：图腾卷轴名称/描述、图腾:任意Ⅲ级名称/描述。");
            }
            catch (Exception ex)
            {
                Debug.LogError("[TotemStorageBag] 注册本地化覆盖失败: " + ex);
            }
        }

        // ---------- 物品注册 ----------

        private static void RegisterTotemBag()
        {
            ItemAssetsCollection collection = ItemAssetsCollection.Instance;
            if (collection == null)
            {
                throw new InvalidOperationException("ItemAssetsCollection.Instance 为 null，无法注册动态物品。");
            }

            int typeID = GetNextFreeTypeID(collection);

            // 参考物品：钥匙包（图标/品质/标签以它为准，价格按本 Mod 设定值）
            ItemAssetsCollection.Entry? reference = FindEntryByDisplayNameKey(collection, "Item_KeyRing")
                ?? FindEntryByDisplayNameKey(collection, "Item_InjectionBag");

            // 运行时创建同名 Tag：Tag.Hash 按 name 计算，与游戏内置 Tag_Totem 资产哈希一致
            Tag totemTag = ScriptableObject.CreateInstance<Tag>();
            totemTag.name = TotemTagName;

            ItemBuilder builder = ItemBuilder.New()
                .TypeID(typeID)
                .DisableStacking();
            for (int i = 0; i < SlotCount; i++)
            {
                builder.Slot("totem_" + i, totemTag, null!);
            }

            Item item = builder.Instantiate();
            item.name = "TotemStorageBag_" + typeID;
            item.DisplayNameRaw = BagDisplayName;
            item.Value = BagRawPrice;
            // 防止切场景时动态物品预制体被销毁，导致后续实例化失败
            GameObject.DontDestroyOnLoad(item.gameObject);

            if (reference != null && reference.metaData.id > 0)
            {
                ItemMetaData meta = reference.metaData;
                _keyRingPrefab = reference.prefab;

                // 优先取预制体上的属性（元数据的 icon 常为 null，会导致背包/仓库里物品不可见）
                item.Icon = (_keyRingPrefab != null && _keyRingPrefab.Icon != null)
                    ? _keyRingPrefab.Icon
                    : meta.icon;
                item.Quality = _keyRingPrefab != null ? _keyRingPrefab.Quality : meta.quality;
                item.DisplayQuality = _keyRingPrefab != null ? _keyRingPrefab.DisplayQuality : meta.displayQuality;

                // 标签对齐参考物品（仓库/背包分类一致）；后续可按需再调整
                if (_keyRingPrefab != null)
                {
                    foreach (Tag tag in _keyRingPrefab.Tags)
                    {
                        item.Tags.Add(tag);
                    }
                }
                Debug.Log($"[TotemStorageBag] 参考物品 {meta.DisplayNameKey}(id={meta.id})：原始价值={item.Value}, 品质={item.Quality}, 图标={(item.Icon != null)}");
            }
            else
            {
                Debug.LogWarning("[TotemStorageBag] 未找到钥匙包参考物品，使用默认图标/品质。");
            }

            // 自定义图标：优先使用 Mod 目录下的图腾卷轴.png
            Sprite? customIcon = LoadIconFromFile(Path.Combine(Application.dataPath, "Mods", ModName, BagIconFileName), BagIconScale);
            if (customIcon != null)
            {
                item.Icon = customIcon;
                Debug.Log("[TotemStorageBag] 已应用自定义图标: " + BagIconFileName);
            }

            // 底色与钥匙包一致（浅红）：原版显示品质 5 会让“价值/稀有度着色”Mod 把动态物品映射为金色。
            // DisplayQuality=6 且 Quality=6 时，该 Mod 的 dynamic/原生分支都映射到 LightRed（钥匙包同款浅红）。
            item.Quality = 6;
            item.DisplayQuality = (DisplayQuality)6;
            Debug.Log("[TotemStorageBag] 图腾卷轴显示品质已设为 6（价值着色 Mod 映射为钥匙包同款浅红底色）。");

            // 重量 1.0kg：原版 Item 无公开 setter，反射写 weight 字段并刷新缓存
            FieldInfo? weightField = typeof(Item).GetField("weight", BindingFlags.Instance | BindingFlags.NonPublic);
            if (weightField != null)
            {
                weightField.SetValue(item, BagWeight);
                item.RecalculateTotalWeight();
                Debug.Log($"[TotemStorageBag] 图腾卷轴重量已设为 {BagWeight} kg");
            }
            else
            {
                Debug.LogWarning("[TotemStorageBag] 未找到 Item.weight 字段，重量设置失败。");
            }

            Tag containerTag = ScriptableObject.CreateInstance<Tag>();
            containerTag.name = ContainerTagName;
            item.Tags.Add(containerTag);

            // 额外标签（默认 Misc=杂物，用于工作台“其他”分区；后续可调整）
            if (_config.ExtraBagTags != null)
            {
                foreach (string tagName in _config.ExtraBagTags)
                {
                    if (string.IsNullOrWhiteSpace(tagName))
                    {
                        continue;
                    }
                    Tag extraTag = ScriptableObject.CreateInstance<Tag>();
                    extraTag.name = tagName.Trim();
                    item.Tags.Add(extraTag);
                }
            }

            // 兜底：确保物品在任何情况下都有可见图标
            if (item.Icon == null)
            {
                item.Icon = CreatePlaceholderIcon();
                Debug.LogWarning("[TotemStorageBag] 参考物品无图标，已生成占位图标。");
            }

            string tagNames = string.Join(",", item.Tags.Select(t => t != null ? t.name : "null"));
            Debug.Log($"[TotemStorageBag] 图腾卷轴标签: [{tagNames}]");

            // ItemBuilder 会顺带创建 Inventory 组件；本包以槽位承载物品（与钥匙包一致），移除多余容器组件避免重复 UI
            if (item.Inventory != null)
            {
                Destroy(item.Inventory);
            }

            if (!ItemAssetsCollection.AddDynamicEntry(item))
            {
                throw new InvalidOperationException("AddDynamicEntry 注册失败。");
            }

            BagTypeID = typeID;
            _bagPrefab = item;
        }

        private static Sprite CreatePlaceholderIcon()
        {
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            Color color = new Color(0.25f, 0.7f, 0.35f, 1f);
            var pixels = new Color[64 * 64];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f));
        }

        /// <summary>
        /// 从磁盘加载 PNG 生成 Sprite（用于自定义物品图标）。
        /// contentScale &lt; 1 时在四周留出透明边距，使图标在格子内视觉上缩小。
        /// </summary>
        private static Sprite? LoadIconFromFile(string path, float contentScale = 1f)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes == null || bytes.Length == 0)
                {
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                if (contentScale <= 0f || contentScale >= 1f)
                {
                    return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                }

                // 等比放大为正方形画布并在中心放入原图：内容占 contentScale 比例，四周留透明边。
                // 正方形画布保证背包格子（方形单元格）与详情页大图显示比例一致。
                int srcW = tex.width;
                int srcH = tex.height;
                int maxDim = Mathf.Max(srcW, srcH);
                int padSize = Mathf.Max(1, Mathf.CeilToInt(maxDim / contentScale));
                var padded = new Texture2D(padSize, padSize, TextureFormat.RGBA32, false);
                var clear = new Color[padSize * padSize];
                for (int i = 0; i < clear.Length; i++)
                {
                    clear[i] = new Color(0f, 0f, 0f, 0f);
                }
                padded.SetPixels(clear);
                padded.SetPixels((padSize - srcW) / 2, (padSize - srcH) / 2, srcW, srcH, tex.GetPixels());
                padded.Apply();
                UnityEngine.Object.Destroy(tex);
                return Sprite.Create(padded, new Rect(0, 0, padSize, padSize), new Vector2(0.5f, 0.5f));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TotemStorageBag] 加载自定义图标失败 (" + path + "): " + ex.Message);
                return null;
            }
        }

        private static ItemAssetsCollection.Entry? FindEntryByDisplayNameKey(ItemAssetsCollection collection, string displayNameKey)
        {
            return FindEntry(collection, displayNameKey, null);
        }

        private static ItemAssetsCollection.Entry? FindEntry(ItemAssetsCollection collection, string displayNameKey, string? namePart)
        {
            if (collection.entries == null)
            {
                return null;
            }
            foreach (ItemAssetsCollection.Entry entry in collection.entries)
            {
                if (entry == null || entry.metaData.id <= 0)
                {
                    continue;
                }
                string key = entry.metaData.DisplayNameKey;
                if (string.Equals(key, displayNameKey, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
                string name = entry.metaData.Name;
                if (!string.IsNullOrEmpty(namePart)
                    && !string.IsNullOrEmpty(name)
                    && name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return entry;
                }
            }
            return null;
        }

        // ---------- 工作台配方 ----------

        private static void RegisterCraftingRecipe()
        {
            if (BagTypeID < 0)
            {
                return;
            }

            try
            {
                CraftingFormulaCollection collection = CraftingFormulaCollection.Instance;
                if (collection == null)
                {
                    Debug.LogWarning("[TotemStorageBag] CraftingFormulaCollection 尚未就绪，配方稍后重试。");
                    return;
                }

                FieldInfo? field = typeof(CraftingFormulaCollection).GetField("list", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null || !(field.GetValue(collection) is List<CraftingFormula> list))
                {
                    Debug.LogError("[TotemStorageBag] 无法访问 CraftingFormulaCollection.list（游戏版本可能已变更）。");
                    return;
                }

                if (list.Any(f => f.id == RecipeId))
                {
                    Debug.Log("[TotemStorageBag] 配方已存在，跳过注册。");
                }
                else
                {
                    // 工作台分类对齐参考物品：若钥匙包有配方，直接复用其 tags（站点/分类完全一致）
                    string[]? referenceTags = FindReferenceRecipeTags();
                    Cost cost = BuildRecipeCost(_config);
                    string[] tags = referenceTags ?? new[] { WorkbenchTagName };

                    list.Add(new CraftingFormula
                    {
                        id = RecipeId,
                        result = new CraftingFormula.ItemEntry { id = BagTypeID, amount = 1 },
                        tags = tags,
                        cost = cost,
                        unlockByDefault = false,
                        hideInIndex = false
                    });
                    Debug.Log($"[TotemStorageBag] 工作台配方已写入：{RecipeId}，站点标签=[{string.Join(",", tags)}]，成本价值={BuildCostValue(cost)}（3×图腾Ⅲ + 20×虚化的羽毛 + {CraftMoneyCost} 合成费）");
                }

                // 配方必须在 CraftingManager 就绪后才能解锁（否则静默失败）；解锁状态随存档持久化
                if (CraftingManager.Instance == null)
                {
                    Debug.LogWarning("[TotemStorageBag] CraftingManager 尚未就绪，配方待稍后解锁。");
                }
                else
                {
                    CraftingManager.UnlockFormula(RecipeId);
                    Debug.Log("[TotemStorageBag] 工作台配方已解锁：TotemStorageBag");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TotemStorageBag] 注册工作台配方失败: " + ex);
            }
        }

        private static long BuildCostValue(Cost cost)
        {
            long total = cost.money;
            if (cost.items != null)
            {
                foreach (Cost.ItemEntry entry in cost.items)
                {
                    int price = 0;
                    if (entry.id > 0)
                    {
                        ItemMetaData meta = ItemAssetsCollection.GetMetaData(entry.id);
                        price = meta.priceEach;
                    }
                    total += price * entry.amount;
                }
            }
            return total;
        }

        private static void DumpTotemItems()
        {
            try
            {
                ItemAssetsCollection collection = ItemAssetsCollection.Instance;
                if (collection == null || collection.entries == null)
                {
                    Debug.LogWarning("[TotemStorageBag] 无法导出图腾清单：ItemAssetsCollection 不可用。");
                    return;
                }

                Tag totemTag = ScriptableObject.CreateInstance<Tag>();
                totemTag.name = TotemTagName;
                var lines = new List<string> { "图腾相关物品清单\tTypeID\t名称\t原始价值\t显示价(÷2)\t品质" };
                int count = 0;
                foreach (ItemAssetsCollection.Entry entry in collection.entries)
                {
                    if (entry == null || entry.metaData.id <= 0 || entry.metaData.tags == null)
                    {
                        continue;
                    }
                    if (!entry.metaData.tags.Any(t => t != null && t.Hash == totemTag.Hash))
                    {
                        continue;
                    }
                    count++;
                    lines.Add($"图腾\t{entry.metaData.id}\t{entry.metaData.DisplayName}\t{entry.metaData.priceEach}\t{entry.metaData.priceEach / 2}\t{entry.metaData.quality}");
                }
                lines.Add($"合计\t{count} 种图腾物品");

                if (_featherTypeID > 0)
                {
                    ItemMetaData featherMeta = ItemAssetsCollection.GetMetaData(_featherTypeID);
                    lines.Add($"材料\t{_featherTypeID}\t虚化的羽毛\t{featherMeta.priceEach}\t{featherMeta.priceEach / 2}\t{featherMeta.quality}");
                }

                Debug.Log("[TotemStorageBag] " + string.Join(" | ", lines));
                try
                {
                    string path = Path.Combine(Application.dataPath, "Mods", ModName, "TotemPrices.txt");
                    File.WriteAllLines(path, lines);
                    Debug.Log("[TotemStorageBag] 图腾价格清单已写入: " + path);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TotemStorageBag] 写入图腾清单文件失败: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TotemStorageBag] 导出图腾清单失败: " + ex.Message);
            }
        }

        private static void LogCraftViewFilters(object craftView)
        {
            try
            {
                FieldInfo? filtersField = typeof(CraftView).GetField("filters", BindingFlags.Instance | BindingFlags.NonPublic);
                if (filtersField == null || !(filtersField.GetValue(craftView) is Array filters))
                {
                    return;
                }

                Type? filterType = typeof(CraftView).GetNestedType("FilterInfo", BindingFlags.Public | BindingFlags.NonPublic);
                if (filterType == null)
                {
                    return;
                }
                FieldInfo? nameField = filterType.GetField("displayNameKey");
                FieldInfo? tagsField = filterType.GetField("requireTags");
                foreach (object item in filters)
                {
                    string key = (string?)nameField?.GetValue(item) ?? "?";
                    Tag[]? tags = tagsField?.GetValue(item) as Tag[];
                    string tagNames = tags == null ? "" : string.Join(",", tags.Select(t => t != null ? t.name : "null"));
                    Debug.Log($"[TotemStorageBag] 工作台分区: {key} -> [{tagNames}]");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TotemStorageBag] 读取工作台分区失败: " + ex.Message);
            }
        }

        private static string[]? FindReferenceRecipeTags()
        {
            try
            {
                CraftingFormulaCollection collection = CraftingFormulaCollection.Instance;
                if (collection == null)
                {
                    return null;
                }

                var allTags = new HashSet<string>();
                string[]? keyRingTags = null;
                foreach (CraftingFormula formula in collection.Entries)
                {
                    if (formula.tags != null)
                    {
                        foreach (string tag in formula.tags)
                        {
                            allTags.Add(tag);
                        }
                    }
                    if (_keyRingPrefab != null && formula.result.id == _keyRingPrefab.TypeID)
                    {
                        keyRingTags = formula.tags;
                    }
                }
                Debug.Log("[TotemStorageBag] 游戏现存配方站点标签: [" + string.Join(",", allTags) + "]");
                return keyRingTags;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TotemStorageBag] 读取参考配方标签失败: " + ex.Message);
                return null;
            }
        }

        private static Cost BuildRecipeCost(ModConfig cfg)
        {
            if (_featherTypeID <= 0)
            {
                throw new InvalidOperationException("未找到虚化的羽毛（Item_Feather）TypeID，无法注册配方。");
            }
            if (_placeholderTypeID <= 0)
            {
                throw new InvalidOperationException("未注册“图腾:任意Ⅲ级”占位材料，无法注册配方。");
            }

            // 成本：3× 占位图腾 + 20× 羽毛 + 2000 合成费，走游戏原生合成显示；
            // 实际制作时由补丁按“任意三个Ⅲ级图腾”校验并消耗真实图腾。
            var entries = new List<(int id, long amount)>
            {
                (_placeholderTypeID, 3),
                (_featherTypeID, FeatherCount)
            };
            return new Cost(CraftMoneyCost, entries.ToArray());
        }

        private static void RegisterTotemMaterialPlaceholder()
        {
            ItemAssetsCollection collection = ItemAssetsCollection.Instance;
            if (collection == null)
            {
                throw new InvalidOperationException("ItemAssetsCollection.Instance 为 null，无法注册占位材料。");
            }

            int typeID = GetNextFreeTypeID(collection);

            Item? iconRef = ItemAssetsCollection.GetPrefab(IconReferenceTypeID);
            ItemBuilder builder = ItemBuilder.New()
                .TypeID(typeID)
                .DisableStacking();
            Item item = builder.Instantiate();
            item.name = "TotemAnyIII_" + typeID;
            item.DisplayNameRaw = AnyTotemDisplayName;
            item.Icon = iconRef != null && iconRef.Icon != null ? iconRef.Icon : CreatePlaceholderIcon();
            // 同步高级图腾的品质与显示品质，让图标按橙色高级图腾渲染，而不是白色低级图腾
            if (iconRef != null)
            {
                item.Quality = iconRef.Quality;
                item.DisplayQuality = iconRef.DisplayQuality;
            }
            item.Value = BagRawPrice;
            GameObject.DontDestroyOnLoad(item.gameObject);

            // 占位材料不参与任何分类：无标签，避免出现在图腾/背包等物品列表
            if (item.Inventory != null)
            {
                Destroy(item.Inventory);
            }

            if (!ItemAssetsCollection.AddDynamicEntry(item))
            {
                throw new InvalidOperationException("AddDynamicEntry 注册占位材料失败。");
            }
            _placeholderTypeID = typeID;
            Debug.Log($"[TotemStorageBag] 占位材料“{AnyTotemDisplayName}”已注册，TypeID={typeID}");
        }

        /// <summary>NextTypeID 不识别已注册的动态物品，这里额外探测动态表，避免 TypeID 冲突互相覆盖。</summary>
        private static int GetNextFreeTypeID(ItemAssetsCollection collection)
        {
            int typeID = collection.NextTypeID;
            while (ItemAssetsCollection.TryGetDynamicEntry(typeID, out _))
            {
                typeID++;
            }
            return typeID;
        }

        private static void ResolveAllowedTotems()
        {
            try
            {
                _allowedTotemTypeIDs.Clear();
                ItemAssetsCollection collection = ItemAssetsCollection.Instance;
                if (collection == null || collection.entries == null)
                {
                    return;
                }

                // 只认“带图腾标签且名称含 Ⅲ”的物品；原版没有Ⅲ级标识，这是运行时约定。
                // 不匹配拉丁 “III”：游戏数据里存在名称含 “III” 的非Ⅲ级物品，会误判。
                Tag totemTag = ScriptableObject.CreateInstance<Tag>();
                totemTag.name = TotemTagName;
                foreach (ItemAssetsCollection.Entry entry in collection.entries)
                {
                    if (entry == null || entry.metaData.id <= 0)
                    {
                        continue;
                    }
                    if (entry.metaData.id == _placeholderTypeID)
                    {
                        continue;
                    }
                    if (entry.metaData.tags == null || !entry.metaData.tags.Any(t => t != null && t.Hash == totemTag.Hash))
                    {
                        continue;
                    }
                    string name = entry.metaData.DisplayName;
                    if (!string.IsNullOrEmpty(name) && name.Contains("Ⅲ"))
                    {
                        _allowedTotemTypeIDs.Add(entry.metaData.id);
                    }
                }
                Debug.Log("[TotemStorageBag] 可作材料的Ⅲ级图腾数量: " + _allowedTotemTypeIDs.Count
                    + "，TypeID=[" + string.Join(",", _allowedTotemTypeIDs.OrderBy(id => id)) + "]");
            }
            catch (Exception ex)
            {
                Debug.LogError("[TotemStorageBag] 解析Ⅲ级图腾列表失败: " + ex);
            }
        }

        private static void ResolveFeatherTypeID()
        {
            try
            {
                ItemAssetsCollection collection = ItemAssetsCollection.Instance;
                if (collection == null)
                {
                    return;
                }
                ItemAssetsCollection.Entry? entry = FindEntry(collection, FeatherDisplayNameKey, "Feather");
                _featherTypeID = entry != null && entry.metaData.id > 0 ? entry.metaData.id : -1;
                if (_featherTypeID > 0)
                {
                    ItemMetaData meta = ItemAssetsCollection.GetMetaData(_featherTypeID);
                    Debug.Log($"[TotemStorageBag] 虚化的羽毛 TypeID={_featherTypeID}，原始价值={meta.priceEach}，显示价={meta.priceEach / 2}");
                }
                else
                {
                    Debug.LogError("[TotemStorageBag] 未找到虚化的羽毛（Item_Feather），配方将无法注册。");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TotemStorageBag] 解析虚化的羽毛失败: " + ex);
            }
        }

        // ---------- 悬停显示（描述覆盖 + ShowInventoryCount 计数修正） ----------

        private static void EnsureHoverSubscribed()
        {
            if (_hoverSubscribed)
            {
                return;
            }
            ItemHoveringUI.onSetupMeta += OnHoverMeta;
            _hoverSubscribed = true;
            Debug.Log("[TotemStorageBag] 已订阅 ItemHoveringUI.onSetupMeta：悬停占位图腾时覆盖描述。");
        }

        private static void OnHoverMeta(ItemHoveringUI ui, ItemMetaData meta)
        {
            // 只处理本 Mod 的占位材料，其余物品完全不动
            if (meta.id != _placeholderTypeID)
            {
                return;
            }
            try
            {
                _hoverDescriptionField ??= typeof(ItemHoveringUI).GetField("itemDescription", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_hoverDescriptionField?.GetValue(ui) is TextMeshProUGUI desc && desc != null)
                {
                    desc.text = AnyTotemDescription;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TotemStorageBag] 覆盖悬停描述失败: " + ex.Message);
            }
        }

        /// <summary>
        /// ShowInventoryCount Mod 按占位物 TypeID 统计数量（恒为 0），
        /// 这里在运行时给它的 Util 计数方法打补丁：占位物按“全部Ⅲ级图腾”统计。
        /// 未安装该 Mod 时补丁自然跳过，不影响本 Mod。
        /// </summary>
        private static void EnsureShowInvCountPatch()
        {
            if (_showInvCountPatched || _showInvCountGaveUp)
            {
                return;
            }
            _showInvCountRetry++;
            try
            {
                MethodInfo? characterMethod = AccessTools.Method("ShowInventoryCount.Util:GetItemCountInCharacterInventory");
                MethodInfo? storageMethod = AccessTools.Method("ShowInventoryCount.Util:GetItemCountInStorage");
                if (characterMethod == null || storageMethod == null)
                {
                    // 约 10 秒（600 帧）内仍找不到，视为未安装 ShowInventoryCount
                    if (_showInvCountRetry > 600)
                    {
                        _showInvCountGaveUp = true;
                        Debug.Log("[TotemStorageBag] 未检测到 ShowInventoryCount Mod，跳过计数修正补丁。");
                    }
                    return;
                }

                var harmony = new Harmony("com.totemstoragebag.showinvcount");
                harmony.Patch(characterMethod,
                    postfix: new HarmonyMethod(typeof(ShowInvCountCountPatch).GetMethod(nameof(ShowInvCountCountPatch.CharacterPostfix))));
                harmony.Patch(storageMethod,
                    postfix: new HarmonyMethod(typeof(ShowInvCountCountPatch).GetMethod(nameof(ShowInvCountCountPatch.StoragePostfix))));
                _showInvCountPatched = true;
                Debug.Log("[TotemStorageBag] ShowInventoryCount 计数补丁已应用：占位图腾显示Ⅲ级图腾总数。");
            }
            catch (Exception ex)
            {
                _showInvCountGaveUp = true;
                Debug.LogWarning("[TotemStorageBag] ShowInventoryCount 计数补丁应用失败: " + ex.Message);
            }
        }

        private static class ShowInvCountCountPatch
        {
            public static void CharacterPostfix(int typeID, ref int __result)
            {
                if (typeID == _placeholderTypeID)
                {
                    __result = CountBackpackTotems();
                }
            }

            public static void StoragePostfix(int typeID, ref int __result)
            {
                if (typeID == _placeholderTypeID)
                {
                    __result = CountStorageTotems();
                }
            }
        }

        /// <summary>统计玩家仓库内的Ⅲ级图腾数量。</summary>
        private static int CountStorageTotems()
        {
            Inventory? storage = PlayerStorage.Inventory;
            if (storage == null)
            {
                return 0;
            }
            return storage.FindAll(e => e != null && _allowedTotemTypeIDs.Contains(e.TypeID)).Sum(e => e.StackCount);
        }

        // ---------- 制作补丁（任意三个Ⅲ级图腾） ----------

        private static void ApplyCraftPatches()
        {
            if (_harmonyApplied)
            {
                return;
            }
            try
            {
                new Harmony("com.totemstoragebag.mod").PatchAll();
                _harmonyApplied = true;
                Debug.Log("[TotemStorageBag] Harmony 补丁已应用：制作接受任意三个Ⅲ级图腾。");
            }
            catch (Exception ex)
            {
                Debug.LogError("[TotemStorageBag] Harmony 补丁应用失败: " + ex);
            }
        }

        [HarmonyPatch(typeof(CraftingManager), "Craft", new[] { typeof(string) })]
        private static class CraftPatch
        {
            private static bool Prefix(string id, ref UniTask<List<Item>> __result)
            {
                if (id != RecipeId)
                {
                    return true; // 非本 Mod 配方，走原逻辑
                }
                __result = TryCraftTotemBag();
                return false;
            }
        }

        [HarmonyPatch]
        private static class ItemAmountDisplayPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                MethodInfo? refresh = typeof(ItemAmountDisplay).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic);
                if (refresh != null)
                {
                    yield return refresh;
                }
            }

            private static void Postfix(ItemAmountDisplay __instance)
            {
                if (__instance == null || __instance.TypeID != _placeholderTypeID || _allowedTotemTypeIDs.Count == 0)
                {
                    return;
                }
                try
                {
                    int amount = Convert.ToInt32(ReadField(__instance, "amount", 3L));
                    int count = CountBackpackTotems();

                    TextMeshProUGUI? amountText = ReadField(__instance, "amountText", null) as TextMeshProUGUI;
                    if (amountText != null)
                    {
                        amountText.text = $"( {count} / {amount} )";
                    }

                    Image? background = ReadField(__instance, "background", null) as Image;
                    Color normal = ReadField(__instance, "normalColor", Color.gray) is Color c1 ? c1 : Color.gray;
                    Color enough = ReadField(__instance, "enoughColor", Color.green) is Color c2 ? c2 : Color.green;
                    if (background != null)
                    {
                        background.color = count >= amount ? enough : normal;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TotemStorageBag] 更新图腾材料计数失败: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 原生可制作判定（cost.Enough → EconomyManager.IsEnough）按占位物 TypeID 统计，恒为不足，
        /// 导致工作台左侧列表底色与“制作”按钮始终为红色/灰色。
        /// 仅当成本含占位物时改写判定：钱 + 虚化的羽毛按持有总量，Ⅲ级图腾按角色背包数量。
        /// </summary>
        [HarmonyPatch(typeof(EconomyManager), "IsEnough")]
        private static class CostEnoughPatch
        {
            private static bool Prefix(Cost cost, ref bool __result)
            {
                if (cost.items == null)
                {
                    return true;
                }
                bool hasPlaceholder = false;
                foreach (Cost.ItemEntry entry in cost.items)
                {
                    if (entry.id == _placeholderTypeID)
                    {
                        hasPlaceholder = true;
                        break;
                    }
                }
                if (!hasPlaceholder)
                {
                    return true; // 非本 Mod 成本走原生判定
                }

                __result = CheckEnough(cost);
                return false;
            }

            private static bool CheckEnough(Cost cost)
            {
                // 合成费：账户 + 现金（与原生口径一致）
                if (EconomyManager.Money + EconomyManager.Cash < cost.money)
                {
                    return false;
                }
                // 其余材料（虚化的羽毛）按持有总量
                if (cost.items != null)
                {
                    foreach (Cost.ItemEntry entry in cost.items)
                    {
                        if (entry.id == _placeholderTypeID)
                        {
                            continue;
                        }
                        if (ItemUtilities.GetItemCount(entry.id) < entry.amount)
                        {
                            return false;
                        }
                    }
                }
                // Ⅲ级图腾：与制作消耗一致，只算角色背包
                return CountBackpackTotems() >= 3;
            }
        }

        /// <summary>
        /// 方案 A：带分类过滤的库存网格在物品变动时会整页重建（加载指示+淡入淡出，即“一瞬间刷新”）。
        /// 改为静默重建：保留原版“按过滤条件重算显示集合”的正确性，但复用已有格子、不做整页释放重建、不闪加载指示。
        /// 仅当 filter != null 时生效；任何异常都回退到原生 LoadEntriesTask。
        /// </summary>
        [HarmonyPatch(typeof(InventoryDisplay), "OnTargetContentChanged")]
        private static class InventoryDisplayRefreshPatch
        {
            private static FieldInfo? _entriesField;
            private static PropertyInfo? _entryPoolProp;
            private static FieldInfo? _entriesParentField;
            private static FieldInfo? _cachedIndexesField;
            private static FieldInfo? _usePagesField;
            private static FieldInfo? _itemsEachPageField;
            private static FieldInfo? _cachedSelectedPageField;
            private static FieldInfo? _activeTaskTokenField;
            private static MethodInfo? _refreshCapacityTextMethod;
            private static MethodInfo? _cacheIndexesMethod;
            private static MethodInfo? _refreshGridMethod;
            private static MethodInfo? _entryRefreshMethod;

            private static bool Prefix(InventoryDisplay __instance)
            {
                if (__instance == null || __instance.Target == null || __instance.Target.Loading)
                {
                    return true;
                }
                // 无过滤器时原生只刷单个格子，无需干预
                if (__instance.filter == null)
                {
                    return true;
                }
                try
                {
                    SilentRebuild(__instance);
                    return false;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TotemStorageBag] 静默重建失败，回退原生刷新: " + ex.Message);
                    return true;
                }
            }

            private static void SilentRebuild(InventoryDisplay display)
            {
                _entriesField ??= typeof(InventoryDisplay).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic);
                _entryPoolProp ??= typeof(InventoryDisplay).GetProperty("EntryPool", BindingFlags.Instance | BindingFlags.NonPublic);
                _entriesParentField ??= typeof(InventoryDisplay).GetField("entriesParent", BindingFlags.Instance | BindingFlags.NonPublic);
                _cachedIndexesField ??= typeof(InventoryDisplay).GetField("cachedIndexesToDisplay", BindingFlags.Instance | BindingFlags.NonPublic);
                _usePagesField ??= typeof(InventoryDisplay).GetField("usePages", BindingFlags.Instance | BindingFlags.NonPublic);
                _itemsEachPageField ??= typeof(InventoryDisplay).GetField("itemsEachPage", BindingFlags.Instance | BindingFlags.NonPublic);
                _cachedSelectedPageField ??= typeof(InventoryDisplay).GetField("cachedSelectedPage", BindingFlags.Instance | BindingFlags.NonPublic);
                _activeTaskTokenField ??= typeof(InventoryDisplay).GetField("activeTaskToken", BindingFlags.Instance | BindingFlags.NonPublic);
                _refreshCapacityTextMethod ??= typeof(InventoryDisplay).GetMethod("RefreshCapacityText", BindingFlags.Instance | BindingFlags.NonPublic);
                _cacheIndexesMethod ??= typeof(InventoryDisplay).GetMethod("CacheIndexesToDisplay", BindingFlags.Instance | BindingFlags.NonPublic);
                _refreshGridMethod ??= typeof(InventoryDisplay).GetMethod("RefreshGridLayoutPreferredHeight", BindingFlags.Instance | BindingFlags.NonPublic);
                _entryRefreshMethod ??= typeof(InventoryEntry).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic);

                if (_entriesField == null || _entryPoolProp == null || _entriesParentField == null || _cachedIndexesField == null
                    || _usePagesField == null || _itemsEachPageField == null || _cachedSelectedPageField == null || _activeTaskTokenField == null
                    || _refreshCapacityTextMethod == null || _cacheIndexesMethod == null || _refreshGridMethod == null || _entryRefreshMethod == null)
                {
                    throw new InvalidOperationException("InventoryDisplay 反射字段解析不完整，游戏版本可能已变更。");
                }

                // 使进行中的旧重建任务失效（与原版 LoadEntriesTask 的 token 机制一致）
                _activeTaskTokenField.SetValue(display, (int)_activeTaskTokenField.GetValue(display)! + 1);

                _refreshCapacityTextMethod.Invoke(display, null);
                _cacheIndexesMethod.Invoke(display, null);
                _refreshGridMethod.Invoke(display, null);

                var entries = (List<InventoryEntry>)_entriesField.GetValue(display)!;
                var pool = (PrefabPool<InventoryEntry>)_entryPoolProp.GetValue(display)!;
                var entriesParent = (Transform)_entriesParentField.GetValue(display)!;
                bool usePages = (bool)_usePagesField.GetValue(display)!;
                int perPage = (int)_itemsEachPageField.GetValue(display)!;
                int page = (int)_cachedSelectedPageField.GetValue(display)!;
                var cachedIndexes = (List<int>)_cachedIndexesField.GetValue(display)!;

                var indexes = new List<int>();
                if (usePages)
                {
                    int begin = page * perPage;
                    int end = Mathf.Min(begin + perPage, cachedIndexes.Count);
                    if (begin < cachedIndexes.Count && begin < end)
                    {
                        for (int i = begin; i < end; i++)
                        {
                            indexes.Add(cachedIndexes[i]);
                        }
                    }
                }
                else
                {
                    indexes.AddRange(cachedIndexes);
                }

                var used = new HashSet<InventoryEntry>();
                int order = 0;
                foreach (int index in indexes)
                {
                    InventoryEntry? entry = entries.FirstOrDefault(e => e != null && e.Index == index);
                    if (entry == null)
                    {
                        entry = pool.Get();
                        entry.gameObject.SetActive(true);
                        entry.Setup(display, index);
                        entry.transform.SetParent(entriesParent, false);
                        entries.Add(entry);
                    }
                    entry.transform.SetSiblingIndex(order);
                    _entryRefreshMethod.Invoke(entry, null);
                    order++;
                    used.Add(entry);
                }

                // 释放不再显示的格子
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    InventoryEntry e = entries[i];
                    if (e == null || !used.Contains(e))
                    {
                        if (e != null)
                        {
                            pool.Release(e);
                        }
                        entries.RemoveAt(i);
                    }
                }
            }
        }

        private static object? ReadField(object target, string name, object? fallback)
        {
            FieldInfo? field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null ? field.GetValue(target) : fallback;
        }

        /// <summary>只统计角色背包（随身背包）内的Ⅲ级图腾数量。</summary>
        private static int CountBackpackTotems()
        {
            Inventory? backpack = LevelManager.Instance?.MainCharacter?.CharacterItem?.Inventory;
            if (backpack == null)
            {
                return 0;
            }
            return backpack.FindAll(e => e != null && _allowedTotemTypeIDs.Contains(e.TypeID)).Sum(e => e.StackCount);
        }


        private static UniTask<List<Item>> TryCraftTotemBag()
        {
            if (BagTypeID < 0 || _featherTypeID <= 0)
            {
                Debug.LogError("[TotemStorageBag] 图腾卷轴或虚化的羽毛未注册，无法制作。");
                return UniTask.FromResult<List<Item>>(null!);
            }

            var moneyCost = new Cost(CraftMoneyCost);
            if (!moneyCost.Enough)
            {
                Debug.Log($"[TotemStorageBag] 合成费不足（需要 {CraftMoneyCost}）。");
                return UniTask.FromResult<List<Item>>(null!);
            }

            Inventory? backpack = LevelManager.Instance?.MainCharacter?.CharacterItem?.Inventory;
            if (backpack == null)
            {
                Debug.Log("[TotemStorageBag] 未找到角色背包，无法制作。");
                return UniTask.FromResult<List<Item>>(null!);
            }

            List<Item> totems = backpack.FindAll(e => e != null && _allowedTotemTypeIDs.Contains(e.TypeID));
            int totemCount = totems.Sum(e => e.StackCount);
            if (totemCount < 3)
            {
                Debug.Log($"[TotemStorageBag] 背包内Ⅲ级图腾不足：需要任意三个（当前 {totemCount}）。");
                return UniTask.FromResult<List<Item>>(null!);
            }

            // 优先消耗价格较低的图腾；价格相同时随机
            totems = totems
                .OrderBy(e => ItemAssetsCollection.GetMetaData(e.TypeID).priceEach)
                .ThenBy(_ => UnityEngine.Random.value)
                .ToList();

            List<Item> feathers = ItemUtilities.FindAllBelongsToPlayer(e => e != null && e.TypeID == _featherTypeID);
            int featherCount = feathers.Sum(e => e.StackCount);
            if (featherCount < FeatherCount)
            {
                Debug.Log($"[TotemStorageBag] 虚化的羽毛不足：需要 {FeatherCount}（当前 {featherCount}）。");
                return UniTask.FromResult<List<Item>>(null!);
            }

            if (!moneyCost.Pay())
            {
                Debug.LogError("[TotemStorageBag] 扣除合成费失败。");
                return UniTask.FromResult<List<Item>>(null!);
            }

            totems.ConsumeItemsOfAmount(3);
            feathers.ConsumeItemsOfAmount(FeatherCount);

            Item bag = ItemAssetsCollection.InstantiateSync(BagTypeID);
            if (bag == null)
            {
                Debug.LogError("[TotemStorageBag] 生成图腾卷轴失败。");
                return UniTask.FromResult<List<Item>>(null!);
            }

            bag.FromInfoKey = "UI_Crafting";
            if (!ItemUtilities.SendToPlayerCharacterInventory(bag))
            {
                ItemUtilities.SendToPlayerStorage(bag);
            }

            if (CraftingFormulaCollection.TryGetFormula(RecipeId, out CraftingFormula formula))
            {
                CraftingManager.OnItemCrafted?.Invoke(formula, bag);
            }
            Debug.Log("[TotemStorageBag] 制作成功：图腾卷轴。");
            return UniTask.FromResult(new List<Item> { bag });
        }

        // ---------- 测试发放 ----------

        private static void TryGiveBagToPlayer()
        {
            if (!_config.GiveItemOnStart || _giveDone || BagTypeID < 0)
            {
                return;
            }
            if (PlayerStorage.Instance == null || PlayerStorage.Inventory == null)
            {
                Debug.Log("[TotemStorageBag] 玩家仓库尚未就绪，等待 OnLoadingFinished 后发放。");
                return;
            }

            GiveBagToPlayer();
        }

        private static void GiveBagToPlayer()
        {
            int count = ItemUtilities.GetItemCount(BagTypeID);
            bool inBuffer = false;
            try
            {
                inBuffer = PlayerStorage.IncomingItemBuffer != null
                    && PlayerStorage.IncomingItemBuffer.Any(t => t != null && t.RootTypeID == BagTypeID);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TotemStorageBag] 读取待领取缓冲失败，跳过缓冲去重: " + ex.Message);
            }
            if (count > 0 || inBuffer)
            {
                Debug.Log($"[TotemStorageBag] 玩家已有图腾卷轴（数量={count}, 缓冲={inBuffer}），跳过发放。");
                _giveDone = true;
                return;
            }

            Item bag = ItemAssetsCollection.InstantiateSync(BagTypeID);
            if (bag == null)
            {
                Debug.LogError("[TotemStorageBag] 生成图腾卷轴实例失败。");
                _giveDone = true;
                return;
            }

            ItemUtilities.SendToPlayerStorage(bag);
            Debug.Log("[TotemStorageBag] 已向玩家仓库发放图腾卷轴。");
            _giveDone = true;
        }

        // ---------- 配置 ----------

        [Serializable]
        private class ModConfig
        {
            public bool GiveItemOnStart = false;
            public string[] ExtraBagTags = Array.Empty<string>();

            public static ModConfig Load()
            {
                string path = Path.Combine(Application.dataPath, "Mods", ModName, "ModConfig.json");
                try
                {
                    if (File.Exists(path))
                    {
                        ModConfig cfg = JsonUtility.FromJson<ModConfig>(File.ReadAllText(path));
                        if (cfg != null)
                        {
                            return cfg;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TotemStorageBag] 读取 ModConfig.json 失败，使用默认配置: " + ex.Message);
                }
                return new ModConfig();
            }
        }
    }
}
