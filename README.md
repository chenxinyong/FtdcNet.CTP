## FtdcNet.CTP

.Net wrapper for ftdc2c_ctp. Support x86/x64/AnyCPU.

## License

Licensed under the [MIT License](http://www.mit-license.org/)

## Version
git branche  => CTP version  
* v6.7.13 &nbsp; => CTP version is V6.7.13 &nbsp; (20260225)
* v6.7.2 &nbsp;  => CTP version is V6.7.2 &nbsp; (20230913) 
* v6.5.1 &nbsp;  => CTP version is V6.5.1 &nbsp; (20200908) 
* v6.3.19 => CTP version is V6.3.19 (20200106) 
* v6.3.15 => CTP version is V6.3.15 (20190220) 

## Use NuGet (recommend)    
  https://www.nuget.org/packages/FtdcNet.CTP  

版本对应
* FtdcNet.CTP.1.5.0.nupkg  => CTP v6.7.13
* FtdcNet.CTP.1.4.0.nupkg  => CTP v6.7.2
* FtdcNet.CTP.1.3.0.nupkg  => CTP v6.5.1
* FtdcNet.CTP.1.2.0.nupkg  => CTP v6.3.19
* FtdcNet.CTP.1.1.0.nupkg  => CTP v6.3.15

按所需CTP版本安装  
* Install-Package FtdcNet.CTP -Version 1.5.0
* Install-Package FtdcNet.CTP -Version 1.4.0
* Install-Package FtdcNet.CTP -Version 1.3.0

NetCore及Net5之后项目，使用NuGet Manager添加Package之后，会自动拷贝所需文件到可执行目录下的runtimes目录

## Build from source   
  you need to specify --recursive to clone submodules as well.  
  git clone --recursive https://github.com/shawn666liu/FtdcNet.CTP.git   

  switch to other branch (other CTP version)  
  for example, switch to v6.3.15  
  git checkout v6.3.15  
  cd ftdc2c_ctp  
  git checkout v6.3.15    

  v6.7.13 分支的 Windows 构建需要 Visual Studio 2026（v145 工具集）；安装依赖后运行：
  powershell tools/build.ps1 -Configuration Release
  详见 tools/README.md。



## Net Framework Project
* Net Framework项目，如果platform是AnyCPU或者是VS2013项目，需要手动拷贝runtimes目录到可执行文件的输出目录，  
  添加方法一： vs项目添加包后，会在C:\\Users\\????\\.nuget\\packages\\ftdcnet.ctp 下保留ftdcnet.ctp.???.nupkg  
  添加方法二： 从https://www.nuget.org/packages/FtdcNet.CTP 页面, 选中合适版本, 点击右边"Download package"下载nupkg包  
  使用7zip或者winzip打开下载后的包，比如 ftdcnet.ctp.1.2.0.nupkg 文件，提取runtimes目录到你的exe所在目录  
* Net Framework项目，如果是VS2019，可以不使用AnyCPU，platform选择x86/x64时，VS会自动复制相应的dll到exe目录，而无需runtimes目录
  

## CZCE 合约代码解析（3 位 / 4 位）

郑商所老品种在前置中通常是 3 位年月（`SA705`），部分配置使用 4 位（`SA2705`）；
新品种（如 `SO2707`）则可能是 4 位。可使用 `CzceInstrumentResolver` 按前置实际合约表解析：

```csharp
using CTP;

var resolver = new CzceInstrumentResolver(traderApi);
// 方式一：启动时一次性加载全部 CZCE 合约（实测 SimNow 2315 条约 0.5 秒）
resolver.LoadAll(++requestId);
string id = resolver.Resolve("SA2705");     // -> "SA705"

// 方式二：按需解析（只查询用到的合约）
string id2 = resolver.ResolveOnDemand("SA2709", () => ++requestId); // -> "SA709"
```

注意：`LoadAll` / `ResolveOnDemand` 会阻塞等待 CTP 查询回调，不能在 CTP 回调线程或 UI 线程调用。
合约是否存在以 `ReqQryInstrument` 结果为准，`SubscribeMarketData` 返回 0 不代表合约一定有效。

## ChangeLog

#### 2026-11-16
- branch v6.7.13 => nuget package 1.5.0
- CTP API v6.7.13_20260225
- 新增 54 个结构体、36 个字段、8 个枚举、24 个回调、17 个原生导出（短信验证码、对冲设置、郑商所套利/套保确认、微信终端信息等）
- broken changes:
  1. `FtdcMdAdapter` / `FtdcTdAdapter` 构造函数新增可选参数 `bIsProductionMode`（默认 `true`）
  2. `SubscribePrivateTopic` 新增可选参数 `nSeqNo`（默认 `1`）
  3. `EnumTeResumeType` 新增 `THOST_TERT_RESUME_FROM_SEQ_NO = 4`
  4. 新增回调 `OnRtnPrivateSeqNo`，其 `Param` 是整数，请使用 `e.Param.ToInt32()`，不要 `Conv.P2S`
  5. 事件枚举序号随 CTP 6.7.13 调整，持久化或硬编码数值的用户请改用枚举名
  6. `ThostFtdcReqUserLoginSCField` 已从 CTP 6.7.13 移除
  7. x86 无 6.7.13 `WinDataCollect.dll`，`CTP_GetSystemInfo` 仅 x64 随包提供

#### 2025-12-26
- branch v6.7.2 => nuget package 1.4.0
- broken changes:  
  1. EnumXXEvent.None removed, use EnumXXEvent.MaxValue as an invalid value  
  2. Some fileds name changed from `Single,Short,Long` to `Single_,Short_,Long_` since they are key words of C#

#### 2021-02-26
- try to fix 'dll not found' issue
- version number updated.

#### 2021-02-09
- ftdc2c_ctp as submodule
- provide v6.3.15/v6.3.19/v6.5.1 branches
- proivde nupkg 

#### 2019-05-12
- ftdc2c_ctp integrated.
- update to CTP v6.3.15