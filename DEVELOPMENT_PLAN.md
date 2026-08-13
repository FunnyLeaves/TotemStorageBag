# DEVELOPMENT_PLAN.md

《逃离鸭科夫》（Escape from Duckov）Mod「TotemStorageBag（图腾收纳包）」开发与执行说明。

本文件是当前开发窗口的执行入口文档，记录项目真实状态、关键路径、技术结论与已完成/待办事项。功能、接口、数据或交互发生变化时，需同步更新本文件；历史版本脉络只在归档文档中保留。

## 1. 项目与游戏基本信息

- 游戏：《逃离鸭科夫》/ Escape from Duckov，Steam AppID：`3167020`
- 开发商：Team Soda；引擎：Unity（Mono），URP 渲染
- Mod 项目：TotemStorageBag（图腾收纳包）；游戏内道具名“图腾卷轴”（项目名不变）
- 代码入口：`src/DuckovMod/DuckovMod/`（MainMod.cs + DuckovMod.csproj），编译产物 `TotemStorageBag.dll`

## 2. 关键路径

| 用途 | 路径 |
|---|---|
| 项目根目录 | `D:\Codex-project\GameMod\Duckov\TotemStorageBag` |
| VS 工程 | `src\DuckovMod\DuckovMod\DuckovMod.csproj` |
| 主代码 | `src\DuckovMod\DuckovMod\MainMod.cs` |
| 游戏安装根 | `E:\steam\steamapps\common\Escape from Duckov` |
| 游戏托管程序集 | `<游戏根>\Duckov_Data\Managed\` |
| Mod 部署目录 | `<游戏根>\Duckov_Data\Mods\TotemStorageBag\` |
| 游戏日志 | `%userprofile%\AppData\LocalLow\TeamSoda\Duckov\Player.log` |
| 图腾价格导出 | `<Mod 部署目录>\TotemPrices.txt`（运行期自动生成） |
| 创意工坊物品 | ID `3782761775`（https://steamcommunity.com/sharedfiles/filedetails/?id=3782761775 ） |

## 3. 技术栈与版本

- Visual Studio 2022 / .NET SDK；工程目标框架 `.NET Standard 2.1`
- Harmony：编译引用 NuGet `Lib.Harmony 2.4.2`；运行时使用随 Mod 部署的 `0Harmony.dll 2.4.1`（netstandard 产物不自动携带，从创意工坊 Mod 3588386576 复制部署）
- Mod 入口：类 `ModBehaviour` 继承 `Duckov.Modding.ModBehaviour`；`info.ini` 的 `name=TotemStorageBag`
- 物品注册：`ItemStatsSystem.ItemAssetsCollection.AddDynamicEntry / RemoveDynamicEntry`
- 本地化：引用 `SodaLocalization.dll`，用 `SodaCraft.Localizations.LocalizationManager` 覆盖文本
- 反编译参考：ILSpy / dnSpy 查看 `Duckov_Data\Managed` 下程序集（关键：ItemStatsSystem.dll、TeamSoda.Duckov.Core.dll）

## 4. 当前功能（当前真实规则）

### 4.1 图腾卷轴（收纳包，TypeID 1651）

- 8 格容器，8 个槽位 `requireTags=[Totem]`，只能放入带“图腾”标签的物品
- 原始价值 18888，商人售价 9444；可出售、不可购买，仅工作台制作
- 图标/品质/标签对齐钥匙包（Item_KeyRing，id=836），额外标签 `Continer` + `Misc`（进入工作台“其他”分区）
- 游戏内名称“图腾卷轴”；制作材料占位图标取抗空间 Ⅲ（TypeID 971，橙色高级图腾）并同步其品质
- 自定义图标：Mod 目录下 `图腾卷轴.png`（运行期 PNG 加载，缺失时回退钥匙包图标）；图标按 0.85 比例在**正方形**画布上留透明边缩小，保证背包格子与详情页显示一致
- 底色：`Quality=6` 且 `DisplayQuality=6`，使“价值/稀有度着色”Mod（ItemLevelAndSearchSoundMod）的 dynamic/原生分支都映射到 LightRed（钥匙包同款浅红），替代原本的金色（Orange）
- 重量 1.0kg：反射写 `Item.weight` 字段并刷新重量缓存
- 名称/描述用 `LocalizationManager.SetOverrideText` 覆盖本地化 key（游戏对未命中 key 会显示 `*key*`，覆盖后去除星号）
- 主菜单 Mod 列表：`info.ini` 的 `displayName=图腾卷轴（图腾收纳包）`，预览图 `preview.png` 为用户提供的 2.png
- 动态 TypeID 分配（`GetNextFreeTypeID` 探测动态表），包=1651、占位材料=1652

### 4.2 工作台配方

- 配方 id=`TotemStorageBag`，站点标签 `WorkBenchAdvanced`
- 材料：3 × 占位“图腾:任意Ⅲ级” + 20 × 虚化的羽毛（TypeID 368） + 2000 合成费
- 配方写入 `CraftingFormulaCollection.list`（反射）并 `CraftingManager.UnlockFormula` 解锁

### 4.3 制作逻辑（Harmony 补丁 `CraftingManager.Craft(string)`）

- 只拦截本 Mod 配方；校验并消耗角色背包内任意 3 个Ⅲ级图腾 + 20 羽毛 + 2000 钱
- Ⅲ级图腾识别：带 Totem 标签且名称含“Ⅲ”（原版无Ⅲ级标记；不匹配拉丁 `III`，避免误判，当前精确 22 种）
- 消耗顺序：价格低者优先，同价随机
- 羽毛/金钱按玩家全部持有统计；成功生成收纳包并发送角色背包，失败回落到仓库
- 原生“可制作”判定（`EconomyManager.IsEnough`）按占位物 TypeID 统计恒为不足，用补丁改写为真实条件判定，使左侧列表底色与“制作”按钮在条件满足时变绿

### 4.4 悬停与计数显示

- 图标计数（补丁 `ItemAmountDisplay.Refresh`）：占位材料显示“(背包Ⅲ级图腾数 / 3)”，只统计角色背包
- 占位图腾悬停：名称“图腾：任意 III 级”、描述“蕴含庞大能量的图腾。”（本地化覆盖实现；另订阅 `ItemHoveringUI.onSetupMeta` 兜底）
- 图腾卷轴悬停：描述“可收纳图腾的卷轴，需要吸收任意三个III级图腾制作。”（本地化覆盖，key 为 `图腾卷轴_Desc`）
- ShowInventoryCount 第三方 Mod 计数修正（运行时补丁 `ShowInventoryCount.Util`）：占位物按Ⅲ级图腾总数统计（背包=角色背包，仓库=玩家仓库）；未安装该 Mod 时自动跳过

## 5. 原生实现 vs Harmony 补丁

- 无需 Harmony、由游戏原生机制实现：物品注册、配方写入/解锁、成本校验（金钱/羽毛按持有统计）、消耗 API、物品发放
- 无需 Harmony、由原生 API 实现：本地化覆盖（`LocalizationManager.SetOverrideText`）、PNG 图标加载、物品重量（反射字段）
- 需要 Harmony 补丁实现：`Craft` 拦截自定义材料校验与消耗、`EconomyManager.IsEnough` 工作台可制作判定（列表/按钮变绿）、`ItemAmountDisplay.Refresh` 图标计数、`ShowInventoryCount.Util` 悬停计数、`InventoryDisplay.OnTargetContentChanged` 静默重建（方案 A，消除过滤网格物品变动时的整页刷新闪烁）
- 悬停描述通过事件订阅实现（无需补丁）

## 6. 构建、部署与测试

1. 编译：`dotnet build src\DuckovMod\DuckovMod\DuckovMod.csproj -c Debug`（需正常权限，MSBuild 要写用户目录缓存）
2. 部署前确认 Duckov 进程未运行（DLL 被占用则无法覆盖）
3. 复制 `bin\Debug\netstandard2.1\TotemStorageBag.dll` 与自定义图标 `图腾卷轴.png` 到 `<Mod 部署目录>\`
4. 启动：`E:\steam\steam.exe -applaunch 3167020`
5. 查看 Player.log 验证：`ModActive_TotemStorageBag=True`、占位 TypeID=1652、Ⅲ级图腾 22 种、各项补丁日志无异常

## 7. 配置

`<Mod 部署目录>\ModConfig.json`：

- `GiveItemOnStart`：开局向仓库发放测试包（当前部署为 `false`，图腾卷轴只能通过制作获得）
- `ExtraBagTags`：额外标签数组（当前 `["Misc"]`）

## 8. 工具清单与下载来源

| 类别 | 工具 | 用途 | 下载来源 |
|---|---|---|---|
| 必备 | Steam 客户端 + 游戏本体 | 开发、测试目标 | [Steam 商店页](https://store.steampowered.com/app/3167020/Escape_From_Duckov/) |
| 必备 | Visual Studio 2022 Community | C# 工程编写与编译 | [Visual Studio 下载](https://visualstudio.microsoft.com/zh-hans/downloads/) |
| 必备 | .NET SDK | dotnet 构建 | [.NET 下载](https://dotnet.microsoft.com/zh-cn/download) |
| 必备 | Git | 版本管理 | [Git 下载](https://git-scm.com/download/win) |
| 必备 | Harmony | 运行时补丁库 | [NuGet: Lib.Harmony](https://www.nuget.org/packages/Lib.Harmony) |
| API 参考 | ILSpy / dnSpy | 反编译游戏 DLL 查接口 | [ILSpy Releases](https://github.com/icsharpcode/ILSpy/releases) / [dnSpyEx Releases](https://github.com/dnSpyEx/dnSpy/releases) |
| 按需 | Unity Hub + Unity 2022.3 LTS | 物品/模型/贴图打包 | [Unity 下载](https://unity.com/download) |
| 按需 | Blender / GIMP / Krita | 建模与贴图 | 各官网 |
| 发布 | SteamCMD | 上传创意工坊 | [SteamCMD 官方 Wiki](https://developer.valvesoftware.com/wiki/SteamCMD) |

## 9. 参考资源

- 官方 Mod 示例（最高优先级）：[xvrsl/duckov_modding](https://github.com/xvrsl/duckov_modding)
- 社区 API 文档（反编译 + AI 生成，需交叉核对）：[xiaomao-miao/duckovAPI](https://github.com/xiaomao-miao/duckovAPI)
- 社区解包源码（注意版权与许可风险）：[obscurefreeman/duckovsrc](https://github.com/obscurefreeman/duckovsrc)
- 原生添加物品/武器教程：[Mr-sans-and-InitLoader-s-team/Duckov-Unity-Mod-Preview](https://github.com/Mr-sans-and-InitLoader-s-team/Duckov-Unity-Mod-Preview)
- 模型 Mod 打包 SDK：[Duckov-Custom-Model/DuckovCustomModel-SDK](https://github.com/Duckov-Custom-Model/DuckovCustomModel-SDK)

## 10. 当前状态与待办

已完成：

- Mod 激活、图腾收纳包物品、8 格图腾容器、工作台“其他”分区配方
- 任意三个Ⅲ级图腾 + 20 羽毛 + 2000 合成费的制作校验与消耗
- 图标计数（0/3）、悬停描述、ShowInventoryCount 计数修正
- 自定义图标（图腾卷轴.png，方形留边）、1.0kg 重量、品质/显示品质 6（浅红底色）、本地化去星号
- 工作台可制作判定变绿（EconomyManager.IsEnough 补丁）、过滤网格静默重建（方案 A）

待办：

- 专属 3D 模型与更多外观自定义（当前为 2D 自定义图标）
- 物品操作后的“一瞬间刷新”：已定位为游戏原生行为（过滤网格整页重建）。方案 A 静默重建补丁已实施（复用格子、无加载指示/淡入淡出），待用户实测确认；方案 B（增量插入/移除格子）留待后续版本
- 制作材料定价复核与价格清单维护
- 创意工坊 v1.0.1：512×512 封面与说明已更新到本地与 GitHub，待重跑 steamcmd 上传使工坊页面生效
- 游戏版本更新后的兼容性复查（反射 / Harmony 补丁易失效）

## 11. 卸载/存档注意事项

- 本 Mod 的物品是运行时动态注册（TypeID 1651/1652），存档依赖 Mod：关闭 Mod 后，仓库/背包中该位置会显示为 0 价值、0 作用的空图标，属游戏对未注册 TypeID 的正常兜底表现；重新启用 Mod 后物品恢复。
- 卸载 Mod 前先出售或移除图腾卷轴，避免留下空图标。

## 12. 交接摘要（新对话入口）

新对话接手本项目的速览，详细规则见上文各节。

### 身份与地址

- 项目：TotemStorageBag（游戏内道具名：图腾卷轴；项目名不变）
- 游戏：Escape from Duckov（Steam AppID 3167020）
- GitHub：`FunnyLeaves/TotemStorageBag`（main 分支；最新 v1.0.1，commit `78b7d24`）
- 创意工坊：ID `3782761775`（标题：图腾卷轴（图腾收纳包））
- 项目根目录：`D:\Codex-project\GameMod\Duckov\TotemStorageBag`

### 关键路径

| 用途 | 路径 |
|---|---|
| 源码 | `src\DuckovMod\DuckovMod\`（DuckovMod.csproj、MainMod.cs、DuckovMod.sln） |
| 发布文件夹 | `publish\TotemStorageBag\`（由 `publish.ps1` 生成，默认 Release） |
| 素材 | `assets\`（图腾卷轴.png 图标、preview.png 512×512 封面、info.ini、ModConfig.json） |
| 本地部署 | `E:\steam\steamapps\common\Escape from Duckov\Duckov_Data\Mods\TotemStorageBag\` |
| 游戏日志 | `%userprofile%\AppData\LocalLow\TeamSoda\Duckov\Player.log` |

### 当前功能

- 8 格图腾容器（TypeID 1651），只容纳 Totem 标签物品；原始价值 18888（售价 9444），可出售不可购买
- 工作台“其他”分区配方：3×占位“图腾:任意Ⅲ级”（TypeID 1652）+ 20 虚化的羽毛（TypeID 368）+ 2000 钱
- 制作补丁：任意 3 个Ⅲ级图腾（Totem 标签且名称含“Ⅲ”，22 种），只消耗角色背包、优先价格较低者、同价随机
- UI：悬停描述/计数（0/3）、ShowInventoryCount 计数修正、工作台可制作判定变绿、过滤网格静默重建（方案 A）
- 自定义图标（方形留边）、重量 1.0kg、品质/显示品质 6（“价值稀有度”Mod 映射为钥匙包同款浅红底色）
- 本地化覆盖去星号；主菜单 Mod 名“图腾卷轴（图腾收纳包）”

### 操作注意

- 发版流程：改代码 → `.\publish.ps1` → 本地测试 → git 提交推送 → `D:\APP\SteamCMD\steamcmd.exe +login <Steam用户名> +workshop_build_item <vdf> +quit`（保持 Steam++ 关闭，手机 App 确认登录）
- git 推送若报 SSL 证书错误（Steam++/网络拦截）：`git -c http.sslVerify=false push`，或先关 Steam++
- 不要同时启用本地部署与工坊订阅的同名 Mod，避免重复注册冲突
- 0Harmony.dll 不入库，`publish.ps1` 从本地部署目录复制
- 沙箱 git 若报 “dubious ownership”：`git config --global --add safe.directory D:/Codex-project/GameMod/Duckov/TotemStorageBag`
- 新对话打开 Codex 时，工作区根目录须指向本项目新路径；旧路径 `D:\Codex-project\project1-GameMod` 已清空，归档后可删除
