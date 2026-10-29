# 股票行情（AShareWidget）

独立的 Windows 11 小组件：在 `Win + W` 看板上显示 A 股**现价**与**涨跌幅**。

- 自包含 C# / .NET 8 Provider，不依赖其它桌面应用
- 支持腾讯与新浪行情，允许在同一个关注列表中混用
- `IQuoteService` 定义统一查询与响应，`QuoteService` 按前缀分流，两个适配器负责请求与解析

## 环境

- Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows SDK（`makeappx` / `signtool`）
- [Windows App Runtime 1.6](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
- 开发者模式，或信任脚本生成的自签证书

## 快速开始

```powershell
cd V:\project\github\ashare-widget

# 只测行情
pwsh scripts/build.ps1
.\publish\win-x64\AShareWidget.Provider.exe -TestFetch

# 首次：管理员信任证书（一次）
pwsh scripts/trust-cert.ps1 -Elevate

# 打包并安装到 Widgets Board
pwsh scripts/sideload.ps1

# 卸载
pwsh scripts/sideload.ps1 -Uninstall
```

装好后：`Win + W` → 添加 **「股票行情」**。

## 改关注股票

配置文件（可手改，不必重装）：

`C:\Users\<你>\AppData\Local\AShareWidget\stocks.json`

（与 `provider.log` 同目录。首次启动会自动生成。）

用有序列表 `codes`，每项必须为 `数据源:原生code`。原生 code 原样传给对应接口，不补市场、不删除点号、不改变大小写。数组顺序即看板显示顺序：

```json
{
  "refreshSeconds": 15,
  "codes": [
    "tencent:sh600519",
    "tencent:usNDX",
    "tencent:jj000834",
    "tencent:hf_XAU",
    "sina:gds_AU9999",
    "sina:nf_AU0"
  ]
}
```

0.3.0 不兼容旧配置：必须删除 `source`，每一项写完整数据源前缀和原生 code。不会自动猜测或修正代码；配置错误会显示刷新失败并保留已有行情，不会偷偷恢复默认列表。最多 12 项，刷新间隔为 5–300 秒。

腾讯适配器解析股票/指数、`jj` 基金净值与 `hf_` 外盘期货；新浪适配器解析沪深北股票、`gds_` 现货、`nf_` 国内期货和 `hf_` 外盘期货。接口是否提供某个具体代码仍以数据源为准。`sina:gds_AU9999` 为人民币克价，`tencent:hf_XAU` 为美元盎司价。

查询按数据源批量发送并恢复列表顺序，单个数据源失败不会影响另一个。统一响应包含数据源、原生代码、名称、价格、涨跌额、涨跌幅、行情时间和错误信息。不根据品种代码补充单位；价格统一显示至少两位、最多四位小数。缺失数值使用空值，显示为 `--`；查询失败时有旧数据则保留并标记缓存。

看板打开时按间隔刷新，关闭后停止查询；保存配置后在下一次需要查询时生效。重新打开或切屏时，未过期数据不查询、不重复推送。
仓库里的 `src/.../stocks.json` 只是打包默认值。

## 排障

日志：`C:\Users\<你>\AppData\Local\AShareWidget\provider.log`

MSIX 进程内不要调用 WASDK `Bootstrap.Initialize`（会报「不支持该请求」）；Runtime 由清单 `PackageDependency` 提供。

0.2.6 修正添加小组件的激活流程：MTA 主线程使用 Win32 消息循环，COM 工厂按请求查询接口，恢复已有小组件和内容更新放到后台，避免阻塞添加回调。

0.2.7 调整行情刷新：首次添加提交初始卡片；重新打开或切屏时，数据未过期就不查询、不推送，过期后后台查询并更新，期间保留 Windows 已有内容。看板打开期间按 `refreshSeconds` 刷新，全部卡片停用后停止定时查询。请求完成后开始计算下一次间隔，失败也按此间隔重试，避免反复打开造成请求堆积。进程重启时保留 Windows 已有卡片，下一次激活时重新获取行情。

0.2.8 将更新时间放入独立的弹性底部容器并靠右对齐，避免股票行数较少时，时间跟随列表停在卡片中间。

0.2.9 添加手动分页，0.2.10 将小、中、大卡片每页数量调整为 2、5、9 只股票，底部左右箭头切换并显示页码。只有一页时隐藏箭头，首尾不循环；行情刷新不自动换页，翻页不发起网络查询。每张卡片独立保存页码，调整大小时尽量保留原先看到的股票。

0.2.12 将小、中、大卡片每页数量调整为 1、5、9 只，并使用全宽空白拉开涨跌额与涨跌幅的距离。

激活回归检查（不安装应用、不请求行情）：

```powershell
dotnet build tests/AShareWidget.ActivationChecks -c Release -p:Platform=x64
& ./tests/AShareWidget.ActivationChecks/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/AShareWidget.ActivationChecks.exe
```

## 目录

| 路径 | 说明 |
|------|------|
| `src/AShareWidget.Provider/` | `IWidgetProvider` + Adaptive Cards + 行情源 |
| `packaging/` | MSIX 清单与图标 |
| `scripts/` | 构建 / 签名 / sideload |
