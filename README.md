# TotemStorageBag（图腾收纳包）

《逃离鸭科夫》（Escape from Duckov，Steam AppID 3167020）Mod。

游戏内道具名：**图腾卷轴**（项目/Mod 名：TotemStorageBag）。

## 功能

- 8 格图腾收纳容器，只能容纳带“图腾”标签的物品
- 原始价值 18888（售价 9444），可出售、不可购买，仅工作台制作
- 制作配方：任意三个Ⅲ级图腾 + 20 虚化的羽毛 + 2000 合成费
- Ⅲ级图腾识别：带 Totem 标签且名称含“Ⅲ”（当前 22 种）
- 制作消耗仅取角色背包内的Ⅲ级图腾，优先消耗价格较低者、同价随机（暂不支持手动指定消耗对象）
- 工作台“其他”分区，图标/名称/描述/重量均做了自定义

## 技术栈

- .NET Standard 2.1 + Harmony（运行时 2.4.x）
- Mod 入口：`Duckov.Modding.ModBehaviour`（info.ini 的 `name=TotemStorageBag`）

## 构建与发布

```powershell
.\publish.ps1            # 编译并生成 publish\TotemStorageBag\ 发布文件夹
```

详细开发说明见 [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)。

## 发布到创意工坊（SteamCMD）

1. 运行 `.\publish.ps1` 生成发布文件夹
2. 修改 `workshop_item.vdf`（首次 `publishedfileid=0`，更新时填已有 ID）
3. 执行：`steamcmd +login <账号> +workshop_build_item <workshop_item.vdf 绝对路径> +quit`

注意：Steam 上传会复写 Mod 内的 `info.ini`。
