# 股票行情（AShareWidget）

独立的 Windows 11 小组件：在 `Win + W` 看板上显示 A 股**现价**与**涨跌幅**。

- 自包含 C# / .NET 8 Provider，不依赖其它桌面应用
- 行情默认走腾讯证券 `http://qt.gtimg.cn/q=`（GBK，`~` 分隔）
- 数据源可切换：`IQuoteSource` + `QuoteSourceFactory`（`stocks.json` 的 `source`）

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

用**一个有序列表** `codes`，条目按腾讯规范 `市场.代码`（`sh` / `sz` / `bj` / `hk` / `us`），数组顺序即看板显示顺序：

```json
{
  "source": "tencent",
  "refreshSeconds": 15,
  "codes": [
    "sh.600519",
    "sz.000001",
    "us.NDX",
    "us.AAPL",
    "hk.00700"
  ]
}
```

也兼容 `sh600519` / `usNDX` / 纯数字等写法；保存后约 15 秒生效。最多 12 只。
仓库里的 `src/.../stocks.json` 只是打包默认值。

## 排障

日志：`C:\Users\<你>\AppData\Local\AShareWidget\provider.log`

MSIX 进程内不要调用 WASDK `Bootstrap.Initialize`（会报「不支持该请求」）；Runtime 由清单 `PackageDependency` 提供。

0.2.6 修正添加小组件的激活流程：MTA 主线程使用 Win32 消息循环，COM 工厂按请求查询接口，恢复已有小组件和内容更新放到后台，避免阻塞添加回调。

0.2.7 调整行情刷新：首次添加提交初始卡片；重新打开或切屏时，数据未过期就不查询、不推送，过期后后台查询并更新，期间保留 Windows 已有内容。看板打开期间按 `refreshSeconds` 刷新，全部卡片停用后停止定时查询。请求完成后开始计算下一次间隔，失败也按此间隔重试，避免反复打开造成请求堆积。进程重启时保留 Windows 已有卡片，下一次激活时重新获取行情。

0.2.8 将更新时间放入独立的弹性底部容器并靠右对齐，避免股票行数较少时，时间跟随列表停在卡片中间。

0.2.9 添加手动分页，0.2.10 将小、中、大卡片每页数量调整为 2、5、9 只股票，底部左右箭头切换并显示页码。只有一页时隐藏箭头，首尾不循环；行情刷新不自动换页，翻页不发起网络查询。每张卡片独立保存页码，调整大小时尽量保留原先看到的股票。

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
