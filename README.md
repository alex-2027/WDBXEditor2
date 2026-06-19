# WDBXEditor2

用于编辑《World of Warcraft》客户端 `DB2` 文件的 Windows 桌面工具。

## 当前状态

这个仓库现在运行在：

- `.NET 10`
- `WPF / Windows`
- 本地 `definitions` 优先，缺失时才尝试从 `WoWDBDefs` 下载对应 `.dbd`

程序目录下会自动使用：

- `definitions/` 作为默认 DBD 定义目录
- `Cache/` 作为网络下载缓存目录

如果你本地网络访问 GitHub 不稳定，建议直接把 `WoWDBDefs/definitions` 复制到程序目录的 `definitions/` 下。

## 已修复

### WDC5 安全保存

此前 `WDC5` 保存路径只支持所有字段都是 `pallet` 压缩，像 Titan 导出的 `SpellCooldowns.db2` 这类 mixed-compression 文件会直接保存失败，提示：

`安全 patch 目前只支持 pallet 压缩的 WDC5 字段`

现在 `WDC5` 安全保存已扩展为按列处理，支持以下压缩类型：

- `None`
- `Immediate`
- `SignedImmediate`
- `Common`
- `Pallet`
- `PalletArray`（当前仅支持 `cardinality = 1` 的简单情况）

保存仍然使用“原地安全 patch”模式，不会改动文件整体大小、记录数、section 数和 layout hash。

## 当前限制

为了尽量避免生成会被 WoW 客户端拒绝的坏文件，`WDC5` 安全保存仍然保留以下限制：

- 仅支持 `indexed non-sparse WDC5`
- 不支持 copied/sparse/multi-section string table
- 字符串字段和字符串数组字段暂不支持安全 patch
- 数组字段只支持长度和布局不变的安全 patch
- `PalletArray` 仅支持原文件里已经存在的数组组合，不能新增 palette 组合
- `Common` 字段如果原文件里没有该 ID 的 common 项，同时你又要写入非默认值，会拒绝保存

这些限制是有意保守，不是单纯“没做完”，目的是优先保证生成文件尽量稳定可用。

## 构建

建议环境：

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2022 或更新版本

构建：

```powershell
dotnet build WDBXEditor2.sln
```

发布 Windows x64：

```powershell
dotnet publish WDBXEditor2/WDBXEditor2.csproj -c Release -r win-x64 --self-contained true
```

## 使用建议

1. 把需要的 `.dbd` 放进程序目录的 `definitions/`
2. 打开目标 `.db2`
3. 优先使用和源文件 build 匹配的 definition
4. 修改后先另存为测试文件
5. 先做客户端验证，再替换正式文件

如果保存时提示 `DBD inline 字段数与 WDC5 column 数不匹配`，通常不是文件已经坏了，而是当前加载时选到的 definition build 和这份 DB2 的实际 layout 不一致。优先尝试：

1. 重新打开该 DB2
2. 在 definition 选择窗口里改用 `自动选择`
3. 如果仍不对，再手动换一个更接近该客户端 build 的 definition

## 说明

- 这是 Windows 程序，不是跨平台 GUI 工具
- 旧版 `WDC4` 仍视为只读，当前版本不会开放保存
- 如果保存失败，优先检查是不是命中了上面的安全限制，而不是继续强行重写整文件
