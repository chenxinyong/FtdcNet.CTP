# tools

CTP 头文件 → C# 生成/校验工具，避免下一次升级再手工导入数千行。

## 前置

- Python 3.8+
- Visual Studio 2026（v145 工具集）用于原生桥接与布局校验
- .NET SDK 11（构建 managed wrapper 与校验程序）

## 目录

- `ctpgen/parse_ctp.py`：解析 `ftdc2c_ctp/api/ThostFtdcUserApiStruct.h` 与
  `ThostFtdcUserApiDataType.h`
- `ctpgen/gen_structs.py`：按 6.7.13 头文件增量更新 `FtdcNet.CTP/Struct.cs`
  （新增类、追加/插入字段、删除上游已移除的类）
- `ctpgen/gen_enums.py`：增量更新 `FtdcNet.CTP/Enums.cs`（新增枚举/枚举值）
- `ctpgen/gen_events.py`：从 `ftdc2c_ctp/enums.h` 重写回调枚举，保证序号与原生一致
- `ctpgen/verify_abi.py`：结构体/字段/事件序号/枚举值/DllImport 全量校验
- `layout-check/`：编译 C++ `sizeof/offsetof` 转储，与 C# `Marshal.SizeOf/OffsetOf` 逐项比对
- `smoke/SmokeTest`：加载原生库、打印 MD/TD API 版本、创建/释放适配器（不登录）
- `live-test/LiveTest`：SimNow/仿真环境联调（登录、订阅、查询、下单/撤单）；凭证只通过命令行参数传入，不写入仓库。
  加 `--resolve` 可用 `CzceInstrumentResolver` 把 4 位 CZCE 代码解析为前置实际代码（如 `SA2705 → SA705`）。
- `soak-test/SoakTest`：夜盘/长稳测试，采集 tick 间隔、回调解析耗时、订单 RTT、查询延迟、
  私有流序号缺口、内存/句柄/线程/CPU，输出 `resources.csv` 与 `summary.json`。
- `soak-test/run-night.ps1`：等待到指定时间后启动 SoakTest，登录失败自动重试，适合夜盘定时跑。
- `tick-collector/TickCollector`：全期货合约 Tick 采集 + 1min K 线合成；Tick 写 DuckDB `ticks` 表，
  K 线写 DuckDB `bars_1m` 表，合约清单写 `contracts.json`。
- `tick-collector/run-night.ps1`：夜盘定时启动全市场采集，登录失败自动重试。
- `vendor/win-x64/WinDataCollect.dll`：CTP 6.7.13 x64 采集库，来自官方 6.7.13 API demo

## 常用命令

```powershell
# 从 6.7.13 头文件增量生成（幂等，可重复运行）
python tools/ctpgen/gen_structs.py
python tools/ctpgen/gen_enums.py
python tools/ctpgen/gen_events.py

# 静态 ABI 校验
python tools/ctpgen/verify_abi.py

# 原生/托管结构体布局逐字段比对
powershell tools/layout-check/run_layout_check.ps1

# 加载原生库并打印 MD/TD 版本（不登录）
dotnet run --project tools/smoke/SmokeTest -p:PlatformTarget=x64
dotnet run --project tools/smoke/SmokeTest -p:PlatformTarget=x86

# 一键：原生 x64/x86 + 校验 + managed 构建 + NuGet 打包
powershell tools/build.ps1 -Configuration Release

# SimNow 联调（示例，凭证请勿提交）
dotnet run --project tools/live-test/LiveTest -- \
  --broker 9999 --user <user> --password <password> \
  --md-front tcp://<md-host>:<port> --td-front tcp://<td-host>:<port> \
  --appid <appid> --authcode <authcode> --instrument cu2612 --place-order

# 夜盘长稳：21:00 自动开始，跑 115 分钟，下单/查询循环
powershell tools/soak-test/run-night.ps1 \
  -User <user> -Password <password> -Broker 9999 \
  -MdFront tcp://<md-host>:<port> -TdFront tcp://<td-host>:<port> \
  -AppId <appid> -AuthCode <authcode> \
  -Instruments cu2612,SA709,TA709,SA705 \
  -DurationSec 6900 -QueryInterval 10 -OrderInterval 20 -PlaceOrder

# 全期货 Tick + 1min 采集（21:00 自动开始，跑 2 小时）
powershell tools/tick-collector/run-night.ps1 \
  -User <user> -Password <password> -Broker 9999 \
  -MdFront tcp://<md-host>:<port> -TdFront tcp://<td-host>:<port> \
  -AppId <appid> -AuthCode <authcode> \
  -DurationSec 7200 -Database data/bars_live.duckdb -Contracts data/contracts.json
```

注意：郑商所（CZCE）老品种在 CTP/SimNow 中的 `InstrumentID` 是 3 位年月，例如
`SA709`、`TA709`、`SA705`；4 位写法（`SA2709` 等）在 SimNow 查不到合约。
新品种（如葵花籽油 `SO2707`）是否可订阅取决于前置是否已经发布该合约；
`SubscribeMarketData` 返回 0 不代表合约存在，应以 `ReqQryInstrument` 结果和实际 tick 为准。
可用 `--list SO` 扫描 CZCE 合约列表。

`gen_structs.py` / `gen_enums.py` 以当前 `Struct.cs` / `Enums.cs` 为基线，只增量修改；
重复运行不会产生重复字段。原生头文件来自 `ftdc2c_ctp` 子模块，升级时应先切换子模块到目标版本。
