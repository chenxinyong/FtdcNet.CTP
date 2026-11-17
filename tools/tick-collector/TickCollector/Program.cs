using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CTP;
using DuckDB.NET.Data;

internal static class Program
{
    private static string _broker = "";
    private static string _user = "";
    private static string _password = "";
    private static string _appId = "";
    private static string _authCode = "";
    private static string _mdFront = "";
    private static string _tdFront = "";
    private static string _dbPath = "data/bars_live.duckdb";
    private static string _contractsPath = "data/contracts.json";
    private static string _parquetPath = "";
    private static int _durationSec = 7200;
    private static int _subscribeBatch = 200;
    private static int _subscribeIntervalMs = 200;
    private static int _queueCapacity = 2_000_000;
    private static int _flushMs = 1000;
    private static bool _dbTest;
    private static bool _aggTest;
    private static DateTime? _minTime;
    private static long _ticksSkipped;

    private static FtdcMdAdapter _md;
    private static FtdcTdAdapter _td;
    private static int _reqId;

    private static readonly ManualResetEventSlim _mdReady = new(false);
    private static readonly ManualResetEventSlim _contractsReady = new(false);
    private static volatile bool _mdOk;
    private static volatile bool _contractsOk;
    private static volatile bool _stopping;

    private static string[] _contracts = Array.Empty<string>();
    private static readonly Dictionary<string, ContractInfo> _contractInfo =
        new(StringComparer.OrdinalIgnoreCase);

    private static Channel<TickSnapshot> _queue;
    private static Thread _consumer;
    private static readonly ConcurrentDictionary<string, BarState> _bars =
        new(StringComparer.OrdinalIgnoreCase);

    private static long _ticksReceived;
    private static long _ticksDropped;
    private static long _ticksWritten;
    private static long _barsWritten;
    private static long _lateTicks;
    private static long _errors;
    private static long _subRequested;
    private static long _subAcked;
    private static long _subErrors;
    private static int _subscribeGate;
    private static long _volumeResets;
    private static long _queueDepth;

    private static readonly Stopwatch _runWatch = new();
    private static DateTime _startedAt;
    private static DateTime _endedAt;

    private static readonly object _dbLock = new();
    private static DuckDBConnection _db;
    private static bool _dbReady;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _stopping = true;
            Log("Ctrl+C received, stopping...");
        };

        try
        {
            ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: TickCollector --broker 9999 --user U --password P " +
                                    "--md-front tcp://ip:port --td-front tcp://ip:port " +
                                    "[--appid X --authcode Y] [--db data/bars_live.duckdb] " +
                                    "[--duration 7200] [--subscribe-batch 200] " +
                                    "[--parquet data/ticks.parquet]");
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? ".");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_contractsPath)) ?? ".");

        if (_aggTest)
        {
            Console.WriteLine("normalize: " +
                $"SA709->{CzceInstrumentCode.Normalize("CZCE", "SA709", DateTime.Today, "20270914")}, " +
                $"SA705->{CzceInstrumentCode.Normalize("CZCE", "SA705", DateTime.Today, "20270514")}, " +
                $"SO2707->{CzceInstrumentCode.Normalize("CZCE", "SO2707", DateTime.Today, "20270714")}, " +
                $"cu2612->{CzceInstrumentCode.Normalize("SHFE", "cu2612", DateTime.Today, "20261214")}");
            OpenDatabase();
            var bars = new List<BarRow>();
            TickSnapshot MakeTick(string time, int ms, double price, int volume) => new TickSnapshot
            {
                InstrumentID = "cu2612", InstrumentID4 = "cu2612", ExchangeID = "SHFE",
                TradingDay = "20261117", ActionDay = "20261117",
                UpdateTime = time, UpdateMillisec = ms,
                LastPrice = price, Volume = volume, Turnover = price * volume,
                OpenInterest = 10000, BidPrice1 = price - 1, BidVolume1 = 1,
                AskPrice1 = price + 1, AskVolume1 = 1,
                UpperLimitPrice = 99999, LowerLimitPrice = 1,
            };
            ProcessTick(MakeTick("21:00:00", 500, 100, 1000), bars);
            ProcessTick(MakeTick("21:00:30", 0, 105, 1005), bars);
            ProcessTick(MakeTick("21:01:00", 100, 103, 1012), bars);
            ProcessTick(MakeTick("21:01:20", 0, 101, 1020), bars);
            FlushAllBars(bars);
            FlushBars(bars);

            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT instrument, bar_time, open, high, low, close, volume, tick_count " +
                              "FROM bars_1m ORDER BY bar_time";
            using var reader = cmd.ExecuteReader();
            var ok = true;
            var rows = 0;
            while (reader.Read())
            {
                rows++;
                var instrument = reader.GetString(0);
                var time = reader.GetDateTime(1);
                var open = reader.GetDouble(2);
                var high = reader.GetDouble(3);
                var low = reader.GetDouble(4);
                var close = reader.GetDouble(5);
                var volume = reader.GetInt64(6);
                var ticks = reader.GetInt32(7);
                Console.WriteLine($"agg-test bar {instrument} {time:HH:mm} " +
                                  $"O={open} H={high} L={low} C={close} V={volume} N={ticks}");
                if (rows == 1 && (open != 100 || high != 105 || low != 100 || close != 105 || volume != 5))
                    ok = false;
                if (rows == 2 && (open != 103 || high != 103 || low != 101 || close != 101 || volume != 8))
                    ok = false;
            }
            Console.WriteLine($"agg-test: {(ok && rows == 2 ? "OK" : "FAIL")}, rows={rows}");
            Shutdown();
            return ok && rows == 2 ? 0 : 1;
        }

        if (_dbTest)
        {
            OpenDatabase();
            FlushTicks(new List<TickSnapshot>
            {
                new TickSnapshot
                {
                    InstrumentID = "cu2612", InstrumentID4 = "cu2612", ExchangeID = "SHFE",
                    TradingDay = "20261117", ActionDay = "20261117",
                    UpdateTime = "21:00:00", UpdateMillisec = 500,
                    LastPrice = 70000, Volume = 100, Turnover = 7000000,
                    OpenInterest = 10000, BidPrice1 = 69990, BidVolume1 = 2,
                    AskPrice1 = 70010, AskVolume1 = 3,
                    UpperLimitPrice = 75000, LowerLimitPrice = 65000,
                },
            });
            FlushBars(new List<BarRow>
            {
                new BarRow
                {
                    InstrumentID = "cu2612", InstrumentCtp = "cu2612",
                    ExchangeID = "SHFE", TradingDay = "20261117",
                    BarTime = new DateTime(2026, 11, 17, 21, 0, 0),
                    Open = 70000, High = 70010, Low = 69990, Close = 70000,
                    Volume = 100, Turnover = 7000000, OpenInterest = 10000,
                    TickCount = 1, UpdatedAt = DateTime.Now,
                },
            });
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT (SELECT count(*) FROM ticks), (SELECT count(*) FROM bars_1m)";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            Console.WriteLine($"db-test ok: ticks={reader.GetInt64(0)}, bars={reader.GetInt64(1)}, db={Path.GetFullPath(_dbPath)}");
            Shutdown();
            return 0;
        }

        _startedAt = DateTime.Now;
        Console.WriteLine($"{_startedAt:yyyy-MM-dd HH:mm:ss} tick collector start");
        Console.WriteLine($"  db={Path.GetFullPath(_dbPath)}");
        Console.WriteLine($"  contracts={Path.GetFullPath(_contractsPath)}");
        Console.WriteLine($"  duration={_durationSec}s, batch={_subscribeBatch}, " +
                          $"interval={_subscribeIntervalMs}ms, queue={_queueCapacity}");

        _queue = Channel.CreateBounded<TickSnapshot>(new BoundedChannelOptions(_queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        _consumer = new Thread(ConsumeLoop) { IsBackground = true, Name = "tick-consumer" };
        _consumer.Start();

        var mdFlow = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? ".", "flow-md");
        var tdFlow = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? ".", "flow-td");
        Directory.CreateDirectory(mdFlow);
        Directory.CreateDirectory(tdFlow);

        _md = new FtdcMdAdapter(mdFlow, false, false);
        _md.OnFrontEvent += MdOnFront;
        _md.OnRspEvent += MdOnRsp;
        _md.OnRtnEvent += MdOnRtn;

        _td = new FtdcTdAdapter(tdFlow);
        _td.OnFrontEvent += TdOnFront;
        _td.OnRspEvent += TdOnRsp;
        _td.OnErrRtnEvent += TdOnErrRtn;
        _td.SubscribePrivateTopic(EnumTeResumeType.THOST_TERT_QUICK);
        _td.SubscribePublicTopic(EnumTeResumeType.THOST_TERT_QUICK);

        _md.RegisterFront(_mdFront);
        _td.RegisterFront(_tdFront);
        _md.Init();
        _td.Init();

        if (!WaitBoth(TimeSpan.FromSeconds(90)))
        {
            Console.Error.WriteLine($"startup failed: mdOk={_mdOk}, contractsOk={_contractsOk}");
            Shutdown();
            return 1;
        }

        StartSubscription("initial");

        _runWatch.Start();
        var lastPrint = Stopwatch.GetTimestamp();
        while (!_stopping && _runWatch.Elapsed.TotalSeconds < _durationSec)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - lastPrint >= 60L * Stopwatch.Frequency)
            {
                lastPrint = now;
                PrintProgress();
            }
            Thread.Sleep(100);
        }

        _stopping = true;
        _endedAt = DateTime.Now;
        Shutdown();
        PrintSummary();
        WriteSummary();
        return 0;
    }

    private static bool WaitBoth(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_mdReady.IsSet && _contractsReady.IsSet)
                return _mdOk && _contractsOk;
            Thread.Sleep(100);
        }
        return _mdOk && _contractsOk && _mdReady.IsSet && _contractsReady.IsSet;
    }

    private static void ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--db-test")
            {
                _dbTest = true;
                continue;
            }
            if (arg == "--agg-test")
            {
                _aggTest = true;
                continue;
            }
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown argument: {arg}");
            var key = arg[2..];
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {arg}");
            values[key] = args[++i];
        }

        if (_dbTest || _aggTest)
        {
            _dbPath = values.GetValueOrDefault("db", _dbPath);
            return;
        }

        _broker = Require(values, "broker");
        _user = Require(values, "user");
        _password = Require(values, "password");
        _mdFront = Require(values, "md-front");
        _tdFront = Require(values, "td-front");
        _appId = values.GetValueOrDefault("appid", "");
        _authCode = values.GetValueOrDefault("authcode", "");
        _dbPath = values.GetValueOrDefault("db", _dbPath);
        _contractsPath = values.GetValueOrDefault("contracts", _contractsPath);
        _parquetPath = values.GetValueOrDefault("parquet", "");

        if (values.TryGetValue("duration", out var d) && int.TryParse(d, out var dv) && dv > 0)
            _durationSec = dv;
        if (values.TryGetValue("subscribe-batch", out var sb) && int.TryParse(sb, out var sbv) && sbv > 0)
            _subscribeBatch = sbv;
        if (values.TryGetValue("subscribe-interval-ms", out var si) && int.TryParse(si, out var siv) && siv >= 0)
            _subscribeIntervalMs = siv;
        if (values.TryGetValue("queue", out var q) && int.TryParse(q, out var qv) && qv > 0)
            _queueCapacity = qv;
        if (values.TryGetValue("flush-ms", out var f) && int.TryParse(f, out var fv) && fv > 0)
            _flushMs = fv;
        if (values.TryGetValue("min-time", out var mt) && !string.IsNullOrWhiteSpace(mt))
        {
            if (DateTime.TryParseExact(mt, "HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var t))
                _minTime = DateTime.Today.Add(t.TimeOfDay);
            else if (DateTime.TryParse(mt, CultureInfo.InvariantCulture,
                         DateTimeStyles.None, out var dt))
                _minTime = dt;
            else
                throw new ArgumentException($"Invalid --min-time: {mt}");
        }
    }

    private static string Require(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required argument --{key}");
        return value;
    }

    private static int NextReqId() => Interlocked.Increment(ref _reqId);

    private static string Err(ThostFtdcRspInfoField info) =>
        info == null ? "null" : $"{info.ErrorID}:{info.ErrorMsg}";

    private static bool IsOk(ThostFtdcRspInfoField info) =>
        info == null || info.ErrorID == 0;

    private static void Log(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    private static void Safe(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log($"[{name}] handler exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ MD

    private static void MdOnFront(object sender, OnFrontEventArgs e) => Safe("MD", () =>
    {
        if (e.EventType == EnumOnFrontType.OnFrontConnected)
        {
            Log("[MD] connected");
            _md.ReqUserLogin(new ThostFtdcReqUserLoginField
            {
                BrokerID = _broker,
                UserID = _user,
                Password = _password,
            }, NextReqId());
            return;
        }
        _mdOk = false;
        Log($"[MD] disconnected reason={e.Reason}");
    });

    private static void MdOnRsp(object sender, OnRspEventArgs e) => Safe("MD", () =>
    {
        if (e.EventType == EnumOnRspType.OnRspUserLogin)
        {
            Log($"[MD] login err={Err(e.RspInfo)}");
            _mdOk = IsOk(e.RspInfo);
            _mdReady.Set();
            if (_mdOk)
                StartSubscription("login");
            return;
        }

        if (e.EventType == EnumOnRspType.OnRspSubMarketData)
        {
            if (!IsOk(e.RspInfo))
            {
                Interlocked.Increment(ref _subErrors);
                if (e.Param != IntPtr.Zero)
                {
                    var f = Conv.P2S<ThostFtdcSpecificInstrumentField>(e.Param);
                    Log($"[MD] subscribe failed {f.InstrumentID}: {Err(e.RspInfo)}");
                }
                return;
            }
            Interlocked.Increment(ref _subAcked);
        }
    });

    private static void MdOnRtn(object sender, OnRtnEventArgs e) => Safe("MD", () =>
    {
        if (e.EventType != EnumOnRtnType.OnRtnDepthMarketData || e.Param == IntPtr.Zero)
            return;

        var t = Conv.P2S<ThostFtdcDepthMarketDataField>(e.Param);
        var tickTs = ParseTimestamp(t.ActionDay, t.UpdateTime, t.UpdateMillisec);
        if (_minTime.HasValue && tickTs < _minTime.Value)
        {
            Interlocked.Increment(ref _ticksSkipped);
            return;
        }
        Interlocked.Increment(ref _ticksReceived);

        var info = _contractInfo.TryGetValue(t.InstrumentID, out var ci) ? ci : null;
        var instrument4 = info != null ? info.InstrumentID4 : t.InstrumentID;
        var exchange = !string.IsNullOrEmpty(t.ExchangeID)
            ? t.ExchangeID
            : (info != null ? info.ExchangeID : "");
        var snap = new TickSnapshot
        {
            InstrumentID = t.InstrumentID,
            InstrumentID4 = instrument4,
            ExchangeID = exchange,
            TradingDay = t.TradingDay,
            ActionDay = t.ActionDay,
            UpdateTime = t.UpdateTime,
            UpdateMillisec = t.UpdateMillisec,
            LastPrice = t.LastPrice,
            Volume = t.Volume,
            Turnover = t.Turnover,
            OpenInterest = t.OpenInterest,
            BidPrice1 = t.BidPrice1,
            BidVolume1 = t.BidVolume1,
            AskPrice1 = t.AskPrice1,
            AskVolume1 = t.AskVolume1,
            UpperLimitPrice = t.UpperLimitPrice,
            LowerLimitPrice = t.LowerLimitPrice,
        };

        if (_queue.Writer.TryWrite(snap))
            Interlocked.Increment(ref _queueDepth);
        else
            Interlocked.Increment(ref _ticksDropped);
    });

    // ------------------------------------------------------------------ TD / contracts

    private static void TdOnFront(object sender, OnFrontEventArgs e) => Safe("TD", () =>
    {
        if (e.EventType != EnumOnFrontType.OnFrontConnected)
        {
            Log($"[TD] disconnected reason={e.Reason}");
            return;
        }
        Log("[TD] connected");
        if (!string.IsNullOrEmpty(_appId))
        {
            _td.ReqAuthenticate(new ThostFtdcReqAuthenticateField
            {
                BrokerID = _broker,
                UserID = _user,
                AppID = _appId,
                AuthCode = _authCode,
            }, NextReqId());
        }
        else
        {
            _td.ReqUserLogin(new ThostFtdcReqUserLoginField
            {
                BrokerID = _broker,
                UserID = _user,
                Password = _password,
            }, NextReqId());
        }
    });

    private static void TdOnRsp(object sender, OnRspEventArgs e) => Safe("TD", () =>
    {
        if (!IsOk(e.RspInfo))
            Log($"[TD] {e.EventType} ERROR req={e.RequestID} err={Err(e.RspInfo)}");

        switch (e.EventType)
        {
            case EnumOnRspType.OnRspAuthenticate:
                if (!IsOk(e.RspInfo))
                {
                    _contractsOk = false;
                    _contractsReady.Set();
                    return;
                }
                Log("[TD] authenticate ok");
                _td.ReqUserLogin(new ThostFtdcReqUserLoginField
                {
                    BrokerID = _broker,
                    UserID = _user,
                    Password = _password,
                }, NextReqId());
                break;

            case EnumOnRspType.OnRspUserLogin:
                if (!IsOk(e.RspInfo))
                {
                    _contractsOk = false;
                    _contractsReady.Set();
                    return;
                }
                Log("[TD] login ok");
                _td.ReqSettlementInfoConfirm(new ThostFtdcSettlementInfoConfirmField
                {
                    BrokerID = _broker,
                    InvestorID = _user,
                }, NextReqId());
                break;

            case EnumOnRspType.OnRspSettlementInfoConfirm:
                if (!IsOk(e.RspInfo))
                {
                    _contractsOk = false;
                    _contractsReady.Set();
                    return;
                }
                Log("[TD] settlement confirmed, querying contracts...");
                new Thread(QueryAllContracts) { IsBackground = true, Name = "contracts" }.Start();
                break;
        }
    });

    private static void TdOnErrRtn(object sender, OnErrRtnEventArgs e) => Safe("TD", () =>
    {
        Interlocked.Increment(ref _errors);
        Log($"[TD] {e.EventType} err={Err(e.RspInfo)}");
    });

    private static void QueryAllContracts()
    {
        try
        {
            var rows = new List<ThostFtdcInstrumentField>();
            Exception error = null;
            using var done = new ManualResetEventSlim(false);
            var requestId = NextReqId();

            void Handler(object sender, OnRspEventArgs e)
            {
                if (e.EventType != EnumOnRspType.OnRspQryInstrument || e.RequestID != requestId)
                    return;
                if (e.RspInfo != null && e.RspInfo.ErrorID != 0)
                {
                    error = new InvalidOperationException($"{e.RspInfo.ErrorID}:{e.RspInfo.ErrorMsg}");
                    done.Set();
                    return;
                }
                if (e.Param != IntPtr.Zero)
                    rows.Add(Conv.P2S<ThostFtdcInstrumentField>(e.Param));
                if (e.IsLast)
                    done.Set();
            }

            _td.OnRspEvent += Handler;
            try
            {
                var ret = _td.ReqQryInstrument(new ThostFtdcQryInstrumentField
                {
                    InstrumentID = "",
                    ExchangeID = "",
                }, requestId);
                if (ret != 0)
                    throw new InvalidOperationException($"ReqQryInstrument returned {ret}");
                if (!done.Wait(TimeSpan.FromMinutes(2)))
                    throw new TimeoutException("contract query timeout");
                if (error != null)
                    throw error;
            }
            finally
            {
                _td.OnRspEvent -= Handler;
            }

            var futures = rows
                .Where(x => x.ProductClass == EnumProductClassType.Futures)
                .Where(x => !string.IsNullOrEmpty(x.InstrumentID))
                .Where(x => !x.InstrumentID.Contains('&'))
                .GroupBy(x => x.InstrumentID, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.InstrumentID, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _contracts = futures.Select(x => x.InstrumentID).ToArray();
            foreach (var f in futures)
            {
                _contractInfo[f.InstrumentID] = new ContractInfo
                {
                    InstrumentID = f.InstrumentID,
                    InstrumentID4 = CzceInstrumentCode.Normalize(
                        f.ExchangeID, f.InstrumentID, DateTime.Today, f.ExpireDate),
                    ExchangeID = f.ExchangeID,
                    IsTrading = f.IsTrading,
                    ExpireDate = f.ExpireDate,
                };
            }

            File.WriteAllText(_contractsPath,
                JsonSerializer.Serialize(new
                {
                    generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    count = _contracts.Length,
                    byExchange = futures.GroupBy(x => x.ExchangeID)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    contracts = futures.Select(x => new
                    {
                        x.InstrumentID,
                        InstrumentID4 = CzceInstrumentCode.Normalize(
                            x.ExchangeID, x.InstrumentID, DateTime.Today, x.ExpireDate),
                        x.ExchangeID,
                        x.IsTrading,
                        x.ExpireDate,
                    }),
                }, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            Log($"[TD] contracts: {_contracts.Length} futures, " +
                string.Join(", ", futures.GroupBy(x => x.ExchangeID)
                    .Select(g => $"{g.Key}={g.Count()}")));

            _contractsOk = _contracts.Length > 0;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log($"[TD] contract discovery failed: {ex.Message}");
            _contractsOk = false;
        }
        finally
        {
            _contractsReady.Set();
        }
    }

    private static void StartSubscription(string reason)
    {
        if (Interlocked.CompareExchange(ref _subscribeGate, 1, 0) != 0)
            return;
        new Thread(() =>
        {
            try
            {
                Log($"[MD] subscribe batch start ({reason})");
                SubscribeAll();
            }
            finally
            {
                Interlocked.Exchange(ref _subscribeGate, 0);
            }
        }) { IsBackground = true, Name = "subscribe" }.Start();
    }

    private static void SubscribeAll()
    {
        try
        {
            if (!_contractsReady.Wait(TimeSpan.FromSeconds(60)) ||
                !_mdReady.Wait(TimeSpan.FromSeconds(60)))
                return;

            for (var offset = 0; offset < _contracts.Length; offset += _subscribeBatch)
            {
                if (_stopping)
                    return;
                var batch = _contracts.Skip(offset).Take(_subscribeBatch).ToArray();
                var ret = _md.SubscribeMarketData(batch);
                Interlocked.Add(ref _subRequested, batch.Length);
                if (ret != 0)
                {
                    Interlocked.Increment(ref _subErrors);
                    Log($"[MD] SubscribeMarketData batch at {offset} returned {ret}");
                }
                if (offset % (_subscribeBatch * 10) == 0)
                    Log($"[MD] subscribe progress {Math.Min(offset + batch.Length, _contracts.Length)}/{_contracts.Length}");
                if (_subscribeIntervalMs > 0)
                    Thread.Sleep(_subscribeIntervalMs);
            }
            Log($"[MD] subscribe requests done: {_subRequested} requested, " +
                $"{_subAcked} acked, {_subErrors} errors");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log($"[MD] subscribe failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ aggregation

    private static void ConsumeLoop()
    {
        try
        {
            OpenDatabase();
            var tickBatch = new List<TickSnapshot>(100_000);
            var barBatch = new List<BarRow>(1000);
            var lastFlush = Stopwatch.GetTimestamp();
            var lastStale = lastFlush;

            while (!_stopping || _queue.Reader.Count > 0)
            {
                var processed = 0;
                while (processed < 50_000 && _queue.Reader.TryRead(out var tick))
                {
                    ProcessTick(tick, barBatch);
                    tickBatch.Add(tick);
                    Interlocked.Decrement(ref _queueDepth);
                    processed++;
                }

                var now = Stopwatch.GetTimestamp();
                if (now - lastFlush >= _flushMs * (double)Stopwatch.Frequency / 1000.0 ||
                    tickBatch.Count >= 100_000)
                {
                    FlushTicks(tickBatch);
                    FlushBars(barBatch);
                    lastFlush = now;
                }

                if (now - lastStale >= Stopwatch.Frequency)
                {
                    FlushStaleBars(barBatch);
                    lastStale = now;
                }

                if (processed == 0)
                    Thread.Sleep(10);
            }

            FlushAllBars(barBatch);
            FlushTicks(tickBatch);
            FlushBars(barBatch);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log($"[consumer] fatal: {ex}");
        }
        finally
        {
            // _db is disposed by Shutdown after optional Parquet export.
        }
    }

    private static void ProcessTick(TickSnapshot t, List<BarRow> barBatch)
    {
        var ts = ParseTimestamp(t.ActionDay, t.UpdateTime, t.UpdateMillisec);
        var minute = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0);

        var instrument = string.IsNullOrEmpty(t.InstrumentID4) ? t.InstrumentID : t.InstrumentID4;
        if (!_bars.TryGetValue(instrument, out var state))
        {
            state = new BarState
            {
                InstrumentID = instrument,
                InstrumentCtp = t.InstrumentID,
            };
            _bars[instrument] = state;
        }

        if (state.HasTick && minute > state.Minute)
        {
            FinalizeBar(state, barBatch);
            state.HasTick = false;
        }

        if (!state.HasTick)
        {
            state.Minute = minute;
            state.InstrumentCtp = t.InstrumentID;
            state.ExchangeID = t.ExchangeID;
            state.TradingDay = t.TradingDay;
            state.ActionDay = t.ActionDay;
            state.Open = t.LastPrice;
            state.High = t.LastPrice;
            state.Low = t.LastPrice;
            state.Close = t.LastPrice;
            state.Volume = 0;
            state.Turnover = 0;
            state.TickCount = 0;
            state.LastCumVolume = t.Volume;
            state.LastCumTurnover = t.Turnover;
            state.HasTick = true;
        }
        else if (minute < state.Minute)
        {
            Interlocked.Increment(ref _lateTicks);
            // Late tick for the just-finalized minute: update OHLC and re-emit.
            if (state.Previous != null && state.Previous.BarTime == minute)
            {
                var prev = state.Previous;
                prev.High = Math.Max(prev.High, t.LastPrice);
                prev.Low = Math.Min(prev.Low, t.LastPrice);
                prev.Close = t.LastPrice;
                prev.OpenInterest = t.OpenInterest;
                prev.TickCount++;
                barBatch.Add(prev);
            }
            return;
        }

        state.High = Math.Max(state.High, t.LastPrice);
        state.Low = Math.Min(state.Low, t.LastPrice);
        state.Close = t.LastPrice;
        state.OpenInterest = t.OpenInterest;
        state.TickCount++;

        var dv = t.Volume - state.LastCumVolume;
        if (dv < 0)
        {
            Interlocked.Increment(ref _volumeResets);
            dv = t.Volume;
        }
        state.Volume += dv;
        state.LastCumVolume = t.Volume;

        var dt = t.Turnover - state.LastCumTurnover;
        if (dt < 0)
            dt = t.Turnover;
        state.Turnover += dt;
        state.LastCumTurnover = t.Turnover;
    }

    private static void FinalizeBar(BarState state, List<BarRow> barBatch)
    {
        if (!state.HasTick || state.TickCount <= 0)
            return;

        var bar = new BarRow
        {
            InstrumentID = state.InstrumentID,
            InstrumentCtp = state.InstrumentCtp,
            ExchangeID = state.ExchangeID,
            TradingDay = state.TradingDay,
            BarTime = state.Minute,
            Open = state.Open,
            High = state.High,
            Low = state.Low,
            Close = state.Close,
            Volume = state.Volume,
            Turnover = state.Turnover,
            OpenInterest = state.OpenInterest,
            TickCount = state.TickCount,
            UpdatedAt = DateTime.Now,
        };
        barBatch.Add(bar);
        state.Previous = bar;
        state.HasTick = false;
    }

    private static void FlushStaleBars(List<BarRow> barBatch)
    {
        var nowMinute = DateTime.Now;
        nowMinute = new DateTime(nowMinute.Year, nowMinute.Month, nowMinute.Day,
            nowMinute.Hour, nowMinute.Minute, 0);

        foreach (var state in _bars.Values)
        {
            if (state.HasTick && state.Minute < nowMinute)
            {
                FinalizeBar(state, barBatch);
            }
        }
    }

    private static void FlushAllBars(List<BarRow> barBatch)
    {
        foreach (var state in _bars.Values)
        {
            if (state.HasTick)
                FinalizeBar(state, barBatch);
        }
    }

    private static DateTime ParseTimestamp(string actionDay, string updateTime, int millisec)
    {
        DateTime day;
        if (!DateTime.TryParseExact(actionDay, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out day))
            day = DateTime.Today;

        if (TimeSpan.TryParse(updateTime, CultureInfo.InvariantCulture, out var time))
            return day.Add(time).AddMilliseconds(millisec);

        return DateTime.Now;
    }

    // ------------------------------------------------------------------ duckdb

    private static void OpenDatabase()
    {
        lock (_dbLock)
        {
            if (_dbReady)
                return;
            _db = new DuckDBConnection($"Data Source={Path.GetFullPath(_dbPath)}");
            _db.Open();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ticks (
    instrument VARCHAR,
    instrument_ctp VARCHAR,
    exchange VARCHAR,
    trading_day VARCHAR,
    action_day VARCHAR,
    ts TIMESTAMP,
    last_price DOUBLE,
    volume BIGINT,
    turnover DOUBLE,
    open_interest DOUBLE,
    bid_price1 DOUBLE,
    bid_volume1 BIGINT,
    ask_price1 DOUBLE,
    ask_volume1 BIGINT,
    upper_limit DOUBLE,
    lower_limit DOUBLE
);
CREATE TABLE IF NOT EXISTS bars_1m (
    instrument VARCHAR NOT NULL,
    instrument_ctp VARCHAR,
    exchange VARCHAR,
    trading_day VARCHAR,
    bar_time TIMESTAMP NOT NULL,
    open DOUBLE,
    high DOUBLE,
    low DOUBLE,
    close DOUBLE,
    volume BIGINT,
    turnover DOUBLE,
    open_interest DOUBLE,
    tick_count INTEGER,
    updated_at TIMESTAMP,
    PRIMARY KEY (instrument, bar_time)
);";
            cmd.ExecuteNonQuery();
            _dbReady = true;
        }
    }

    private static void FlushTicks(List<TickSnapshot> batch)
    {
        if (batch.Count == 0)
            return;
        lock (_dbLock)
        {
            if (!_dbReady)
                return;
            try
            {
                using var appender = _db.CreateAppender("ticks");
                foreach (var t in batch)
                {
                    var row = appender.CreateRow();
                    row.AppendValue(string.IsNullOrEmpty(t.InstrumentID4) ? t.InstrumentID : t.InstrumentID4)
                        .AppendValue(t.InstrumentID)
                        .AppendValue(t.ExchangeID)
                        .AppendValue(t.TradingDay)
                        .AppendValue(t.ActionDay)
                        .AppendValue(ParseTimestamp(t.ActionDay, t.UpdateTime, t.UpdateMillisec))
                        .AppendValue(t.LastPrice)
                        .AppendValue((long)t.Volume)
                        .AppendValue(t.Turnover)
                        .AppendValue(t.OpenInterest)
                        .AppendValue(t.BidPrice1)
                        .AppendValue((long)t.BidVolume1)
                        .AppendValue(t.AskPrice1)
                        .AppendValue((long)t.AskVolume1)
                        .AppendValue(t.UpperLimitPrice)
                        .AppendValue(t.LowerLimitPrice)
                        .EndRow();
                }
                appender.Close();
                Interlocked.Add(ref _ticksWritten, batch.Count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                Log($"[db] tick flush failed: {ex.Message}");
            }
            finally
            {
                batch.Clear();
            }
        }
    }

    private static void FlushBars(List<BarRow> batch)
    {
        if (batch.Count == 0)
            return;
        lock (_dbLock)
        {
            if (!_dbReady)
                return;
            try
            {
                var sql = new StringBuilder();
                sql.Append("INSERT OR REPLACE INTO bars_1m VALUES ");
                for (var i = 0; i < batch.Count; i++)
                {
                    var b = batch[i];
                    if (i > 0) sql.Append(',');
                    sql.Append('(')
                        .Append(Quote(b.InstrumentID)).Append(',')
                        .Append(Quote(b.InstrumentCtp)).Append(',')
                        .Append(Quote(b.ExchangeID)).Append(',')
                        .Append(Quote(b.TradingDay)).Append(',')
                        .Append(Quote(b.BarTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
                        .Append(b.Open.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                        .Append(b.High.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                        .Append(b.Low.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                        .Append(b.Close.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                        .Append(b.Volume).Append(',')
                        .Append(b.Turnover.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                        .Append(b.OpenInterest.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                        .Append(b.TickCount).Append(',')
                        .Append(Quote(b.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
                        .Append(')');
                }
                using var cmd = _db.CreateCommand();
                cmd.CommandText = sql.ToString();
                cmd.ExecuteNonQuery();
                Interlocked.Add(ref _barsWritten, batch.Count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                Log($"[db] bar flush failed: {ex.Message}");
            }
            finally
            {
                batch.Clear();
            }
        }
    }

    private static string Quote(string value) =>
        "'" + (value ?? "").Replace("'", "''") + "'";

    private static void Shutdown()
    {
        _stopping = true;
        try { _queue?.Writer.TryComplete(); } catch { }
        try { _consumer?.Join(TimeSpan.FromSeconds(10)); } catch { }

        if (!string.IsNullOrEmpty(_parquetPath))
        {
            try
            {
                lock (_dbLock)
                {
                    if (_dbReady)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_parquetPath)) ?? ".");
                        using var cmd = _db.CreateCommand();
                        cmd.CommandText = $"COPY ticks TO {Quote(Path.GetFullPath(_parquetPath))} " +
                                          "(FORMAT PARQUET, COMPRESSION ZSTD)";
                        cmd.ExecuteNonQuery();
                        Log($"[db] exported ticks to {_parquetPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[db] parquet export failed: {ex.Message}");
            }
        }

        lock (_dbLock)
        {
            try { _db?.Dispose(); } catch { }
            _db = null;
            _dbReady = false;
        }

        try { _md?.Dispose(); } catch { }
        try { _td?.Dispose(); } catch { }
    }

    private static void PrintProgress()
    {
        Log($"progress {_runWatch.Elapsed.TotalMinutes:F1}m contracts={_contracts.Length} " +
            $"sub={_subAcked}/{_subRequested} ticks={_ticksReceived} written={_ticksWritten} " +
            $"bars={_barsWritten} queue={_queue.Reader.Count} dropped={_ticksDropped} " +
            $"skipped={_ticksSkipped} late={_lateTicks} err={_errors}");
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("===== tick collector summary =====");
        Console.WriteLine($"duration: {_runWatch.Elapsed.TotalMinutes:F2} min");
        Console.WriteLine($"contracts: {_contracts.Length}");
        Console.WriteLine($"subscribe: requested={_subRequested} acked={_subAcked} errors={_subErrors}");
        Console.WriteLine($"ticks: received={_ticksReceived} written={_ticksWritten} " +
                          $"dropped={_ticksDropped} skipped={_ticksSkipped} " +
                          $"late={_lateTicks} queue={_queue.Reader.Count}");
        Console.WriteLine($"bars written: {_barsWritten}");
        Console.WriteLine($"volume resets: {_volumeResets}, errors: {_errors}");
        Console.WriteLine($"db: {Path.GetFullPath(_dbPath)}");
    }

    private static void WriteSummary()
    {
        try
        {
            var summary = new
            {
                startedAt = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                endedAt = _endedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                durationSec = _runWatch.Elapsed.TotalSeconds,
                contracts = _contracts.Length,
                subscribe = new { requested = _subRequested, acked = _subAcked, errors = _subErrors },
                ticks = new
                {
                    received = _ticksReceived,
                    written = _ticksWritten,
                    dropped = _ticksDropped,
                    skipped = _ticksSkipped,
                    late = _lateTicks,
                    volumeResets = _volumeResets,
                },
                barsWritten = _barsWritten,
                errors = _errors,
                db = Path.GetFullPath(_dbPath),
            };
            File.WriteAllText(Path.ChangeExtension(Path.GetFullPath(_dbPath), ".summary.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"summary write failed: {ex.Message}");
        }
    }

    private sealed class BarState
    {
        public string InstrumentID;
        public string InstrumentCtp;
        public string ExchangeID;
        public string TradingDay;
        public string ActionDay;
        public DateTime Minute;
        public bool HasTick;
        public double Open, High, Low, Close;
        public long Volume;
        public double Turnover;
        public double OpenInterest;
        public int TickCount;
        public long LastCumVolume;
        public double LastCumTurnover;
        public BarRow Previous;
    }

    private sealed class BarRow
    {
        public string InstrumentID;
        public string InstrumentCtp;
        public string ExchangeID;
        public string TradingDay;
        public DateTime BarTime;
        public double Open, High, Low, Close;
        public long Volume;
        public double Turnover;
        public double OpenInterest;
        public int TickCount;
        public DateTime UpdatedAt;
    }

    private sealed class ContractInfo
    {
        public string InstrumentID;
        public string InstrumentID4;
        public string ExchangeID;
        public int IsTrading;
        public string ExpireDate;
    }

    private struct TickSnapshot
    {
        public string InstrumentID;
        public string InstrumentID4;
        public string ExchangeID;
        public string TradingDay;
        public string ActionDay;
        public string UpdateTime;
        public int UpdateMillisec;
        public double LastPrice;
        public int Volume;
        public double Turnover;
        public double OpenInterest;
        public double BidPrice1;
        public int BidVolume1;
        public double AskPrice1;
        public int AskVolume1;
        public double UpperLimitPrice;
        public double LowerLimitPrice;
    }
}
