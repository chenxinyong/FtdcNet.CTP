using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CTP;

internal static class Program
{
    private static string _broker = "";
    private static string _user = "";
    private static string _password = "";
    private static string _appId = "";
    private static string _authCode = "";
    private static string _mdFront = "";
    private static string _tdFront = "";
    private static string[] _instruments = Array.Empty<string>();
    private static int _durationSec = 3600;
    private static int _queryIntervalSec = 10;
    private static int _orderIntervalSec = 20;
    private static bool _placeOrder;
    private static string _outputDir = "";
    private static double _gapThresholdSec = 5.0;

    private static FtdcMdAdapter _md;
    private static FtdcTdAdapter _td;
    private static int _reqId;
    private static ThostFtdcRspUserLoginField _login;

    private static readonly ManualResetEventSlim _mdReady = new(false);
    private static readonly ManualResetEventSlim _tdReady = new(false);
    private static volatile bool _mdOk;
    private static volatile bool _tdOk;
    private static volatile bool _stopping;

    private static readonly ConcurrentDictionary<string, InstrumentStat> _stats =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, InstrumentInfo> _infos =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, double> _lastPrice =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, QueryStart> _pendingQueries = new();
    private static readonly ConcurrentDictionary<string, long> _pendingOrders = new();
    private static readonly List<ResourceSample> _resources = new();
    private static readonly object _resourceLock = new();

    private static readonly Histogram _tickParse = new(0.01, 10000, 50.0); // 0-50ms
    private static readonly Histogram _orderRtt = new(1.0, 5000, 5000.0);   // 0-5s
    private static readonly Histogram _queryLat = new(1.0, 5000, 5000.0);

    private static long _ticks;
    private static long _errors;
    private static long _seqGaps;
    private static long _mdDisconnects;
    private static long _tdDisconnects;
    private static long _lastDisconnectLogTs;
    private static long _lastSeq;
    private static long _orderSent;
    private static long _orderFilled;
    private static long _orderCanceled;
    private static long _orderRejected;
    private static long _orderTimeout;
    private static long _querySent;
    private static long _queryOk;
    private static long _queryFailed;

    private static readonly object _orderLock = new();
    private static string _activeOrderRef;
    private static string _activeOrderInstrument;
    private static long _activeOrderTs;
    private static bool _activeOrderCancelSent;

    private static readonly object _seqLock = new();
    private static readonly Stopwatch _runWatch = new();
    private static DateTime _startedAt;
    private static DateTime _endedAt;
    private static int _orderRefCounter;

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
            Console.Error.WriteLine("Usage: SoakTest --broker 9999 --user U --password P " +
                                    "--md-front tcp://ip:port --td-front tcp://ip:port " +
                                    "--instruments cu2612,SA709 [--appid X --authcode Y] " +
                                    "[--duration 3600] [--query-interval 10] " +
                                    "[--order-interval 20] [--place-order] [--output dir]");
            return 2;
        }

        Directory.CreateDirectory(_outputDir);
        _startedAt = DateTime.Now;
        Console.WriteLine($"{_startedAt:yyyy-MM-dd HH:mm:ss} soak start");
        Console.WriteLine($"  instruments: {string.Join(",", _instruments)}");
        Console.WriteLine($"  duration: {_durationSec}s, queryInterval={_queryIntervalSec}s, " +
                          $"orderInterval={_orderIntervalSec}s, placeOrder={_placeOrder}");
        Console.WriteLine($"  output: {_outputDir}");

        var mdFlow = Path.Combine(_outputDir, "flow-md");
        var tdFlow = Path.Combine(_outputDir, "flow-td");
        Directory.CreateDirectory(mdFlow);
        Directory.CreateDirectory(tdFlow);

        _md = new FtdcMdAdapter(mdFlow, false, false);
        _md.OnFrontEvent += MdOnFront;
        _md.OnRspEvent += MdOnRsp;
        _md.OnRtnEvent += MdOnRtn;

        _td = new FtdcTdAdapter(tdFlow);
        _td.OnFrontEvent += TdOnFront;
        _td.OnRspEvent += TdOnRsp;
        _td.OnRtnEvent += TdOnRtn;
        _td.OnErrRtnEvent += TdOnErrRtn;
        _td.SubscribePrivateTopic(EnumTeResumeType.THOST_TERT_QUICK);
        _td.SubscribePublicTopic(EnumTeResumeType.THOST_TERT_QUICK);

        _md.RegisterFront(_mdFront);
        _td.RegisterFront(_tdFront);
        _md.Init();
        _td.Init();

        if (!WaitBoth(TimeSpan.FromSeconds(40)))
        {
            Console.Error.WriteLine("login/settlement timeout or failure: " +
                                    $"mdOk={_mdOk}, tdOk={_tdOk}");
            Shutdown();
            return 1;
        }

        _runWatch.Start();
        using var resourceTimer = new Timer(SampleResources, null, 0, 5000);
        var lastPrint = Stopwatch.GetTimestamp();
        var nextQuery = Stopwatch.GetTimestamp();
        var nextOrder = Stopwatch.GetTimestamp() + (long)(_orderIntervalSec * Stopwatch.Frequency);

        while (!_stopping && _runWatch.Elapsed.TotalSeconds < _durationSec)
        {
            var now = Stopwatch.GetTimestamp();

            if (_queryIntervalSec > 0 && now >= nextQuery)
            {
                SendQuery();
                nextQuery = now + (long)(_queryIntervalSec * Stopwatch.Frequency);
            }

            if (_placeOrder && _orderIntervalSec > 0 && now >= nextOrder)
            {
                TryPlaceOrder();
                nextOrder = now + (long)(_orderIntervalSec * Stopwatch.Frequency);
            }

            TimeoutPendingQueries(now);
            TimeoutActiveOrder(now);

            if (now - lastPrint >= 60L * Stopwatch.Frequency)
            {
                lastPrint = now;
                PrintProgress();
            }

            Thread.Sleep(50);
        }

        CancelActiveOrder("shutdown");
        Thread.Sleep(2000);
        _endedAt = DateTime.Now;
        resourceTimer.Dispose();
        SampleResources(null);
        Shutdown();
        WriteResults();
        PrintSummary();
        return 0;
    }

    private static bool WaitBoth(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_mdReady.IsSet && _tdReady.IsSet)
                return _mdOk && _tdOk;
            Thread.Sleep(100);
        }
        return _mdOk && _tdOk && _mdReady.IsSet && _tdReady.IsSet;
    }

    private static void ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--place-order")
            {
                _placeOrder = true;
                continue;
            }
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown argument: {arg}");
            var key = arg[2..];
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {arg}");
            values[key] = args[++i];
        }

        _broker = Require(values, "broker");
        _user = Require(values, "user");
        _password = Require(values, "password");
        _mdFront = Require(values, "md-front");
        _tdFront = Require(values, "td-front");
        _appId = values.GetValueOrDefault("appid", "");
        _authCode = values.GetValueOrDefault("authcode", "");
        var instruments = values.GetValueOrDefault("instruments", "cu2612");
        // CZCE 习惯大写（SA709），SHFE/DCE 习惯小写（cu2612），保留调用方写法。
        _instruments = instruments.Split(new[] { ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_instruments.Length == 0)
            throw new ArgumentException("--instruments is empty");

        if (values.TryGetValue("duration", out var duration) &&
            int.TryParse(duration, out var d) && d > 0)
            _durationSec = d;
        if (values.TryGetValue("query-interval", out var qi) &&
            int.TryParse(qi, out var q) && q >= 0)
            _queryIntervalSec = q;
        if (values.TryGetValue("order-interval", out var oi) &&
            int.TryParse(oi, out var o) && o >= 0)
            _orderIntervalSec = o;
        if (values.TryGetValue("gap-threshold", out var gap) &&
            double.TryParse(gap, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g > 0)
            _gapThresholdSec = g;

        _outputDir = values.GetValueOrDefault("output", "");
        if (string.IsNullOrWhiteSpace(_outputDir))
            _outputDir = Path.Combine("tools", "soak-test", "results",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        _outputDir = Path.GetFullPath(_outputDir);
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
            Log($"[MD] connected");
            _md.ReqUserLogin(new ThostFtdcReqUserLoginField
            {
                BrokerID = _broker,
                UserID = _user,
                Password = _password,
            }, NextReqId());
            return;
        }

        Interlocked.Increment(ref _mdDisconnects);
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastDisconnectLogTs);
        if (last == 0 || now - last > 5L * Stopwatch.Frequency)
        {
            Interlocked.Exchange(ref _lastDisconnectLogTs, now);
            Log($"[MD] disconnected reason={e.Reason} " +
                $"(md={Interlocked.Read(ref _mdDisconnects)}, td={Interlocked.Read(ref _tdDisconnects)})");
        }
    });

    private static void MdOnRsp(object sender, OnRspEventArgs e) => Safe("MD", () =>
    {
        if (e.EventType == EnumOnRspType.OnRspUserLogin)
        {
            Log($"[MD] login err={Err(e.RspInfo)}");
            if (!IsOk(e.RspInfo))
            {
                _mdOk = false;
                _mdReady.Set();
                return;
            }
            _mdOk = true;
            _mdReady.Set();
            var ret = _md.SubscribeMarketData(_instruments);
            Log($"[MD] SubscribeMarketData({string.Join(",", _instruments)}) -> {ret}");
        }
        else if (e.EventType == EnumOnRspType.OnRspSubMarketData)
        {
            if (e.Param != IntPtr.Zero)
            {
                var f = Conv.P2S<ThostFtdcSpecificInstrumentField>(e.Param);
                Log($"[MD] subscribed {f.InstrumentID}");
            }
        }
    });

    private static void MdOnRtn(object sender, OnRtnEventArgs e) => Safe("MD", () =>
    {
        if (e.EventType != EnumOnRtnType.OnRtnDepthMarketData || e.Param == IntPtr.Zero)
            return;

        var sw = Stopwatch.GetTimestamp();
        var tick = Conv.P2S<ThostFtdcDepthMarketDataField>(e.Param);
        var parseMs = (Stopwatch.GetTimestamp() - sw) * 1000.0 / Stopwatch.Frequency;
        _tickParse.Record(parseMs);
        Interlocked.Increment(ref _ticks);

        _lastPrice[tick.InstrumentID] = tick.LastPrice;

        var now = Stopwatch.GetTimestamp();
        var stat = _stats.GetOrAdd(tick.InstrumentID, id => new InstrumentStat { Id = id });
        lock (stat)
        {
            if (stat.LastTs != 0)
            {
                var gap = (now - stat.LastTs) / (double)Stopwatch.Frequency;
                if (gap > stat.MaxGapSec)
                    stat.MaxGapSec = gap;
                if (gap >= _gapThresholdSec && _runWatch.Elapsed.TotalSeconds > 10)
                    stat.GapCount++;
            }
            stat.LastTs = now;
            stat.Count++;
        }
    });

    // ------------------------------------------------------------------ TD

    private static void TdOnFront(object sender, OnFrontEventArgs e) => Safe("TD", () =>
    {
        if (e.EventType != EnumOnFrontType.OnFrontConnected)
        {
            Interlocked.Increment(ref _tdDisconnects);
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastDisconnectLogTs);
            if (last == 0 || now - last > 5L * Stopwatch.Frequency)
            {
                Interlocked.Exchange(ref _lastDisconnectLogTs, now);
                Log($"[TD] disconnected reason={e.Reason} " +
                    $"(md={Interlocked.Read(ref _mdDisconnects)}, td={Interlocked.Read(ref _tdDisconnects)})");
            }
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
            ReqLogin();
        }
    });

    private static void ReqLogin() =>
        _td.ReqUserLogin(new ThostFtdcReqUserLoginField
        {
            BrokerID = _broker,
            UserID = _user,
            Password = _password,
        }, NextReqId());

    private static void TdOnRsp(object sender, OnRspEventArgs e) => Safe("TD", () =>
    {
        if (!IsOk(e.RspInfo))
        {
            Interlocked.Increment(ref _errors);
            Log($"[TD] {e.EventType} ERROR req={e.RequestID} err={Err(e.RspInfo)}");
        }

        switch (e.EventType)
        {
            case EnumOnRspType.OnRspAuthenticate:
                if (!IsOk(e.RspInfo))
                {
                    _tdOk = false;
                    _tdReady.Set();
                    return;
                }
                Log("[TD] authenticate ok");
                ReqLogin();
                break;

            case EnumOnRspType.OnRspUserLogin:
                if (!IsOk(e.RspInfo))
                {
                    _tdOk = false;
                    _tdReady.Set();
                    return;
                }
                if (e.Param != IntPtr.Zero)
                    _login = Conv.P2S<ThostFtdcRspUserLoginField>(e.Param);
                if (_login == null)
                {
                    _tdOk = false;
                    _tdReady.Set();
                    return;
                }
                Log($"[TD] login ok tradingDay={_login.TradingDay} front={_login.FrontID} " +
                    $"session={_login.SessionID} maxOrderRef={_login.MaxOrderRef}");
                _td.ReqSettlementInfoConfirm(new ThostFtdcSettlementInfoConfirmField
                {
                    BrokerID = _broker,
                    InvestorID = _user,
                }, NextReqId());
                break;

            case EnumOnRspType.OnRspSettlementInfoConfirm:
                if (!IsOk(e.RspInfo))
                {
                    _tdOk = false;
                    _tdReady.Set();
                    return;
                }
                Log("[TD] settlement confirmed");
                new Thread(DiscoverInstruments) { IsBackground = true }.Start();
                break;

            case EnumOnRspType.OnRspQryTradingAccount:
                if (e.Param != IntPtr.Zero && e.IsLast)
                {
                    var a = Conv.P2S<ThostFtdcTradingAccountField>(e.Param);
                    Log($"[TD] account balance={a.Balance:F2} available={a.Available:F2} " +
                        $"margin={a.CurrMargin:F2}");
                }
                CompleteQuery(e);
                break;

            case EnumOnRspType.OnRspQryInvestorPosition:
                CompleteQuery(e);
                break;

            case EnumOnRspType.OnRspOrderInsert:
                if (!IsOk(e.RspInfo))
                {
                    Interlocked.Increment(ref _orderRejected);
                    ClearActiveOrder();
                }
                break;

            case EnumOnRspType.OnRspOrderAction:
                if (!IsOk(e.RspInfo))
                {
                    Log($"[TD] order action error: {Err(e.RspInfo)}");
                    ClearActiveOrder();
                }
                break;
        }
    });

    private static void DiscoverInstruments()
    {
        try
        {
            foreach (var id in _instruments)
            {
                var info = QueryInstrument(id, NextReqId());
                if (info == null)
                {
                    Log($"[TD] instrument {id} not found; using defaults");
                    _infos[id] = new InstrumentInfo { InstrumentID = id, ExchangeID = GuessExchange(id), PriceTick = 1 };
                    continue;
                }
                _infos[id] = new InstrumentInfo
                {
                    InstrumentID = info.InstrumentID,
                    ExchangeID = info.ExchangeID,
                    PriceTick = info.PriceTick > 0 ? info.PriceTick : 1,
                    IsTrading = info.IsTrading,
                };
                Log($"[TD] {id}: exchange={info.ExchangeID} tick={info.PriceTick} trading={info.IsTrading}");
            }
            _tdOk = true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log($"[TD] instrument discovery failed: {ex.Message}");
            _tdOk = false;
        }
        finally
        {
            _tdReady.Set();
        }
    }

    private static ThostFtdcInstrumentField QueryInstrument(string id, int requestId)
    {
        ThostFtdcInstrumentField found = null;
        Exception error = null;
        using var done = new ManualResetEventSlim(false);

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
            {
                var f = Conv.P2S<ThostFtdcInstrumentField>(e.Param);
                if (string.Equals(f.InstrumentID, id, StringComparison.OrdinalIgnoreCase))
                    found = f;
            }
            if (e.IsLast)
                done.Set();
        }

        _td.OnRspEvent += Handler;
        try
        {
            var ret = _td.ReqQryInstrument(new ThostFtdcQryInstrumentField
            {
                InstrumentID = id,
                ExchangeID = "",
            }, requestId);
            if (ret != 0)
                throw new InvalidOperationException($"ReqQryInstrument returned {ret}");
            if (!done.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException($"instrument query timeout: {id}");
            if (error != null)
                throw error;
            return found;
        }
        finally
        {
            _td.OnRspEvent -= Handler;
        }
    }

    private static string GuessExchange(string id)
    {
        var p = new string(id.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        return p switch
        {
            "CU" or "AL" or "ZN" or "PB" or "NI" or "SN" or "AU" or "AG" or
            "RB" or "WR" or "HC" or "SS" or "SP" or "BU" or "FU" or "RU" or
            "AO" or "BR" => "SHFE",
            "SC" or "LU" or "NR" or "BC" => "INE",
            "SA" or "TA" or "MA" or "FG" or "UR" or "SF" or "SM" or "AP" or
            "CJ" or "PK" or "PF" or "SH" or "PX" or "PR" or "SR" or "CF" or
            "CY" or "OI" or "RM" or "WH" or "PM" or "RI" or "LR" or "JR" or
            "RS" or "ZC" or "SO" => "CZCE",
            "IF" or "IH" or "IC" or "IM" or "T" or "TF" or "TS" => "CFFEX",
            _ => "DCE",
        };
    }

    private static void TdOnRtn(object sender, OnRtnEventArgs e) => Safe("TD", () =>
    {
        switch (e.EventType)
        {
            case EnumOnRtnType.OnRtnPrivateSeqNo:
                var seq = e.Param.ToInt32();
                lock (_seqLock)
                {
                    if (_lastSeq != 0 && seq != _lastSeq + 1)
                        Interlocked.Increment(ref _seqGaps);
                    _lastSeq = seq;
                }
                break;

            case EnumOnRtnType.OnRtnOrder:
                if (e.Param == IntPtr.Zero) return;
                HandleOrder(Conv.P2S<ThostFtdcOrderField>(e.Param));
                break;

            case EnumOnRtnType.OnRtnTrade:
                if (e.Param == IntPtr.Zero) return;
                var trade = Conv.P2S<ThostFtdcTradeField>(e.Param);
                Log($"[TD] trade {trade.InstrumentID} price={trade.Price} vol={trade.Volume} " +
                    $"orderRef={trade.OrderRef}");
                lock (_orderLock)
                {
                    if (!string.IsNullOrEmpty(_activeOrderRef) && trade.OrderRef == _activeOrderRef)
                    {
                        Interlocked.Increment(ref _orderFilled);
                        ClearActiveOrderLocked();
                    }
                }
                break;
        }
    });

    private static void HandleOrder(ThostFtdcOrderField order)
    {
        lock (_orderLock)
        {
            if (string.IsNullOrEmpty(_activeOrderRef) || order.OrderRef != _activeOrderRef)
                return;

            if (_pendingOrders.TryRemove(order.OrderRef, out var sentTs))
            {
                var ms = (Stopwatch.GetTimestamp() - sentTs) * 1000.0 / Stopwatch.Frequency;
                _orderRtt.Record(ms);
            }

            if (order.OrderStatus == EnumOrderStatusType.NoTradeQueueing ||
                order.OrderStatus == EnumOrderStatusType.PartTradedQueueing)
            {
                if (!_activeOrderCancelSent)
                {
                    _activeOrderCancelSent = true;
                    var ret = _td.ReqOrderAction(new ThostFtdcInputOrderActionField
                    {
                        BrokerID = _broker,
                        InvestorID = _user,
                        UserID = _user,
                        InstrumentID = order.InstrumentID,
                        ExchangeID = order.ExchangeID,
                        FrontID = _login.FrontID,
                        SessionID = _login.SessionID,
                        OrderRef = order.OrderRef,
                        ActionFlag = EnumActionFlagType.Delete,
                    }, NextReqId());
                    if (ret != 0)
                    {
                        Interlocked.Increment(ref _orderRejected);
                        ClearActiveOrderLocked();
                    }
                }
                return;
            }

            if (order.OrderStatus == EnumOrderStatusType.Canceled)
            {
                if (!string.IsNullOrEmpty(order.StatusMsg) && order.StatusMsg.Contains("拒绝", StringComparison.Ordinal))
                    Interlocked.Increment(ref _orderRejected);
                else
                    Interlocked.Increment(ref _orderCanceled);
                ClearActiveOrderLocked();
            }
            else if (order.OrderStatus == EnumOrderStatusType.AllTraded)
            {
                Interlocked.Increment(ref _orderFilled);
                ClearActiveOrderLocked();
            }
            else if (order.OrderStatus == EnumOrderStatusType.NoTradeNotQueueing)
            {
                Interlocked.Increment(ref _orderRejected);
                ClearActiveOrderLocked();
            }
        }
    }

    private static void TdOnErrRtn(object sender, OnErrRtnEventArgs e) => Safe("TD", () =>
    {
        Interlocked.Increment(ref _errors);
        Log($"[TD] {e.EventType} err={Err(e.RspInfo)}");
        if (e.EventType == EnumOnErrRtnType.OnErrRtnOrderInsert ||
            e.EventType == EnumOnErrRtnType.OnErrRtnOrderAction)
        {
            lock (_orderLock)
            {
                Interlocked.Increment(ref _orderRejected);
                ClearActiveOrderLocked();
            }
        }
    });

    // ------------------------------------------------------------------ queries / orders

    private static void SendQuery()
    {
        if (_pendingQueries.Count > 0)
            return;

        var requestId = NextReqId();
        var account = (_querySent % 2) == 0;
        _pendingQueries[requestId] = new QueryStart
        {
            SentTs = Stopwatch.GetTimestamp(),
            Kind = account ? "account" : "position",
        };
        Interlocked.Increment(ref _querySent);

        var ret = account
            ? _td.ReqQryTradingAccount(new ThostFtdcQryTradingAccountField
            {
                BrokerID = _broker,
                InvestorID = _user,
            }, requestId)
            : _td.ReqQryInvestorPosition(new ThostFtdcQryInvestorPositionField
            {
                BrokerID = _broker,
                InvestorID = _user,
            }, requestId);

        if (ret != 0)
        {
            _pendingQueries.TryRemove(requestId, out _);
            Interlocked.Increment(ref _queryFailed);
        }
    }

    private static void CompleteQuery(OnRspEventArgs e)
    {
        if (e.RequestID == 0 || !_pendingQueries.TryGetValue(e.RequestID, out var start))
            return;

        if (!IsOk(e.RspInfo))
        {
            if (_pendingQueries.TryRemove(e.RequestID, out _))
                Interlocked.Increment(ref _queryFailed);
            return;
        }

        if (!e.IsLast)
            return;

        if (_pendingQueries.TryRemove(e.RequestID, out _))
        {
            var ms = (Stopwatch.GetTimestamp() - start.SentTs) * 1000.0 / Stopwatch.Frequency;
            _queryLat.Record(ms);
            Interlocked.Increment(ref _queryOk);
        }
    }

    private static void TimeoutPendingQueries(long now)
    {
        foreach (var kv in _pendingQueries)
        {
            var age = (now - kv.Value.SentTs) / (double)Stopwatch.Frequency;
            if (age > 30 && _pendingQueries.TryRemove(kv.Key, out _))
                Interlocked.Increment(ref _queryFailed);
        }
    }

    private static void TryPlaceOrder()
    {
        lock (_orderLock)
        {
            if (!string.IsNullOrEmpty(_activeOrderRef))
                return;

            string instrument = null;
            double price = 0;
            foreach (var id in _instruments)
            {
                if (!_lastPrice.TryGetValue(id, out var last) || last <= 0)
                    continue;
                var info = _infos.GetValueOrDefault(id);
                var tick = info?.PriceTick > 0 ? info.PriceTick : 1;
                instrument = id;
                price = Math.Round(last * 0.98 / tick) * tick;
                break;
            }
            if (instrument == null || price <= 0)
                return;

            var info2 = _infos.GetValueOrDefault(instrument);
            var exchange = info2?.ExchangeID ?? GuessExchange(instrument);
            var orderRef = NextOrderRef();
            var order = new ThostFtdcInputOrderField
            {
                BrokerID = _broker,
                InvestorID = _user,
                UserID = _user,
                InstrumentID = instrument,
                ExchangeID = exchange,
                OrderRef = orderRef,
                OrderPriceType = EnumOrderPriceTypeType.LimitPrice,
                Direction = EnumDirectionType.Buy,
                CombOffsetFlag_0 = EnumOffsetFlagType.Open,
                CombHedgeFlag_0 = EnumHedgeFlagType.Speculation,
                LimitPrice = price,
                VolumeTotalOriginal = 1,
                TimeCondition = EnumTimeConditionType.GFD,
                VolumeCondition = EnumVolumeConditionType.AV,
                MinVolume = 1,
                ContingentCondition = EnumContingentConditionType.Immediately,
                ForceCloseReason = EnumForceCloseReasonType.NotForceClose,
                IsAutoSuspend = 0,
                UserForceClose = 0,
            };

            _activeOrderRef = orderRef;
            _activeOrderInstrument = instrument;
            _activeOrderTs = Stopwatch.GetTimestamp();
            _activeOrderCancelSent = false;
            _pendingOrders[orderRef] = _activeOrderTs;
            Interlocked.Increment(ref _orderSent);

            var ret = _td.ReqOrderInsert(order, NextReqId());
            if (ret != 0)
            {
                Interlocked.Increment(ref _orderRejected);
                ClearActiveOrderLocked();
                return;
            }
            Log($"[TD] order {orderRef} {instrument} buy open @ {price}");
        }
    }

    private static string NextOrderRef()
    {
        if (_login != null && long.TryParse(_login.MaxOrderRef, out var max))
            return (max + 1000 + Interlocked.Increment(ref _orderRefCounter)).ToString();
        return (1000 + Interlocked.Increment(ref _orderRefCounter)).ToString();
    }

    private static void TimeoutActiveOrder(long now)
    {
        string orderRef = null;
        string instrument = null;
        lock (_orderLock)
        {
            if (string.IsNullOrEmpty(_activeOrderRef))
                return;
            if ((now - _activeOrderTs) / (double)Stopwatch.Frequency <= 20)
                return;
            orderRef = _activeOrderRef;
            instrument = _activeOrderInstrument;
            Interlocked.Increment(ref _orderTimeout);
            ClearActiveOrderLocked();
        }
        Log($"[TD] order {orderRef} timeout");
        SendCancel(orderRef, instrument, "timeout");
    }

    private static void CancelActiveOrder(string reason)
    {
        string orderRef;
        string instrument;
        lock (_orderLock)
        {
            if (string.IsNullOrEmpty(_activeOrderRef))
                return;
            orderRef = _activeOrderRef;
            instrument = _activeOrderInstrument;
            ClearActiveOrderLocked();
        }
        SendCancel(orderRef, instrument, reason);
    }

    private static void SendCancel(string orderRef, string instrument, string reason)
    {
        if (_login == null)
            return;
        var info = _infos.GetValueOrDefault(instrument);
        var ret = _td.ReqOrderAction(new ThostFtdcInputOrderActionField
        {
            BrokerID = _broker,
            InvestorID = _user,
            UserID = _user,
            InstrumentID = instrument,
            ExchangeID = info?.ExchangeID ?? GuessExchange(instrument),
            FrontID = _login.FrontID,
            SessionID = _login.SessionID,
            OrderRef = orderRef,
            ActionFlag = EnumActionFlagType.Delete,
        }, NextReqId());
        Log($"[TD] cancel {orderRef} ({reason}) -> {ret}");
    }

    private static void ClearActiveOrder()
    {
        lock (_orderLock)
            ClearActiveOrderLocked();
    }

    private static void ClearActiveOrderLocked()
    {
        if (!string.IsNullOrEmpty(_activeOrderRef))
            _pendingOrders.TryRemove(_activeOrderRef, out _);
        _activeOrderRef = null;
        _activeOrderInstrument = null;
        _activeOrderCancelSent = false;
    }

    // ------------------------------------------------------------------ resources / output

    private static long _lastCpuMs;
    private static long _lastCpuTs;

    private static void SampleResources(object state)
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            var now = Stopwatch.GetTimestamp();
            var cpuMs = (long)p.TotalProcessorTime.TotalMilliseconds;
            double cpuPct = 0;
            if (_lastCpuTs != 0)
            {
                var elapsedMs = (now - _lastCpuTs) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs > 0)
                    cpuPct = (cpuMs - _lastCpuMs) / elapsedMs / Environment.ProcessorCount * 100.0;
            }
            _lastCpuMs = cpuMs;
            _lastCpuTs = now;

            var sample = new ResourceSample
            {
                Time = DateTime.Now,
                WorkingSetMB = p.WorkingSet64 / 1024.0 / 1024.0,
                PrivateMB = p.PrivateMemorySize64 / 1024.0 / 1024.0,
                ManagedMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0,
                Handles = p.HandleCount,
                Threads = p.Threads.Count,
                Gen0 = GC.CollectionCount(0),
                Gen1 = GC.CollectionCount(1),
                Gen2 = GC.CollectionCount(2),
                CpuPct = cpuPct,
            };
            lock (_resourceLock)
                _resources.Add(sample);
        }
        catch
        {
            // ignore sampling failures
        }
    }

    private static void PrintProgress()
    {
        var mem = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
        Log($"progress {_runWatch.Elapsed.TotalMinutes:F1}m ticks={Interlocked.Read(ref _ticks)} " +
            $"orders={Interlocked.Read(ref _orderSent)} canceled={Interlocked.Read(ref _orderCanceled)} " +
            $"queries={Interlocked.Read(ref _queryOk)}/{Interlocked.Read(ref _querySent)} " +
            $"seqGaps={Interlocked.Read(ref _seqGaps)} errors={Interlocked.Read(ref _errors)} " +
            $"managed={mem:F1}MB");
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("===== soak summary =====");
        Console.WriteLine($"duration: {(_runWatch.Elapsed.TotalMinutes):F2} min");
        Console.WriteLine($"ticks: {Interlocked.Read(ref _ticks)}");
        Console.WriteLine($"tick parse ms: {_tickParse}");
        Console.WriteLine($"order sent/canceled/filled/rejected/timeout: " +
                          $"{_orderSent}/{_orderCanceled}/{_orderFilled}/{_orderRejected}/{_orderTimeout}");
        Console.WriteLine($"order rtt ms: {_orderRtt}");
        Console.WriteLine($"query sent/ok/fail: {_querySent}/{_queryOk}/{_queryFailed}");
        Console.WriteLine($"query latency ms: {_queryLat}");
        Console.WriteLine($"private seq gaps: {_seqGaps}");
        Console.WriteLine($"disconnects: md={_mdDisconnects}, td={_tdDisconnects}");
        Console.WriteLine($"errors: {_errors}");
        foreach (var stat in _stats.Values.OrderBy(x => x.Id))
            Console.WriteLine($"  {stat.Id}: ticks={stat.Count} maxGap={stat.MaxGapSec:F2}s gaps>={_gapThresholdSec}s:{stat.GapCount}");
        if (_resources.Count > 0)
        {
            var first = _resources[0];
            var last = _resources[^1];
            Console.WriteLine($"memory: workingSet {first.WorkingSetMB:F1}->{last.WorkingSetMB:F1} MB, " +
                              $"managed {first.ManagedMB:F1}->{last.ManagedMB:F1} MB, " +
                              $"handles {first.Handles}->{last.Handles}, threads {first.Threads}->{last.Threads}");
        }
        Console.WriteLine($"output: {_outputDir}");
    }

    private static void WriteResults()
    {
        try
        {
            var csv = new StringBuilder();
            csv.AppendLine("time,working_set_mb,private_mb,managed_mb,handles,threads,gc0,gc1,gc2,cpu_pct");
            lock (_resourceLock)
            {
                foreach (var r in _resources)
                {
                    csv.AppendLine(string.Join(",",
                        r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        r.WorkingSetMB.ToString("F1", CultureInfo.InvariantCulture),
                        r.PrivateMB.ToString("F1", CultureInfo.InvariantCulture),
                        r.ManagedMB.ToString("F1", CultureInfo.InvariantCulture),
                        r.Handles, r.Threads, r.Gen0, r.Gen1, r.Gen2,
                        r.CpuPct.ToString("F1", CultureInfo.InvariantCulture)));
                }
            }
            File.WriteAllText(Path.Combine(_outputDir, "resources.csv"), csv.ToString(), Encoding.UTF8);

            var summary = new
            {
                startedAt = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                endedAt = _endedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                durationSec = _runWatch.Elapsed.TotalSeconds,
                broker = _broker,
                user = _user,
                instruments = _instruments,
                ticks = _ticks,
                tickParseMs = _tickParse.ToSummary(),
                orders = new
                {
                    sent = _orderSent,
                    canceled = _orderCanceled,
                    filled = _orderFilled,
                    rejected = _orderRejected,
                    timeout = _orderTimeout,
                    rttMs = _orderRtt.ToSummary(),
                },
                queries = new
                {
                    sent = _querySent,
                    ok = _queryOk,
                    failed = _queryFailed,
                    latencyMs = _queryLat.ToSummary(),
                },
                privateSeqGaps = _seqGaps,
                disconnects = new { md = _mdDisconnects, td = _tdDisconnects },
                errors = _errors,
                perInstrument = _stats.Values.OrderBy(x => x.Id).Select(x => new
                {
                    instrument = x.Id,
                    ticks = x.Count,
                    maxGapSec = x.MaxGapSec,
                    gaps = x.GapCount,
                }).ToArray(),
                resources = _resources.Count > 0
                    ? new
                    {
                        first = _resources[0],
                        last = _resources[^1],
                        maxWorkingSetMB = _resources.Max(r => r.WorkingSetMB),
                        maxPrivateMB = _resources.Max(r => r.PrivateMB),
                        maxManagedMB = _resources.Max(r => r.ManagedMB),
                        maxHandles = _resources.Max(r => r.Handles),
                        maxThreads = _resources.Max(r => r.Threads),
                    }
                    : null,
            };
            File.WriteAllText(Path.Combine(_outputDir, "summary.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"write results failed: {ex.Message}");
        }
    }

    private static void Shutdown()
    {
        try { _md?.Dispose(); } catch { }
        try { _td?.Dispose(); } catch { }
    }

    private sealed class InstrumentStat
    {
        public string Id;
        public long Count;
        public long LastTs;
        public double MaxGapSec;
        public long GapCount;
    }

    private sealed class InstrumentInfo
    {
        public string InstrumentID;
        public string ExchangeID;
        public double PriceTick;
        public int IsTrading;
    }

    private sealed class QueryStart
    {
        public long SentTs;
        public string Kind;
    }

    private sealed class ResourceSample
    {
        public DateTime Time { get; set; }
        public double WorkingSetMB { get; set; }
        public double PrivateMB { get; set; }
        public double ManagedMB { get; set; }
        public int Handles { get; set; }
        public int Threads { get; set; }
        public int Gen0 { get; set; }
        public int Gen1 { get; set; }
        public int Gen2 { get; set; }
        public double CpuPct { get; set; }
    }

    private sealed class Histogram
    {
        private readonly double _bucketMs;
        private readonly long[] _counts;
        private readonly double _maxValueMs;
        private readonly object _sync = new object();
        private long _count;
        private double _sum;
        private double _max;

        public Histogram(double bucketMs, int buckets, double maxValueMs)
        {
            _bucketMs = bucketMs;
            _counts = new long[buckets];
            _maxValueMs = maxValueMs;
        }

        public void Record(double valueMs)
        {
            if (valueMs < 0 || double.IsNaN(valueMs) || double.IsInfinity(valueMs))
                return;
            var index = (int)(valueMs / _bucketMs);
            if (index >= _counts.Length)
                index = _counts.Length - 1;
            lock (_sync)
            {
                _counts[index]++;
                _count++;
                _sum += valueMs;
                if (valueMs > _max)
                    _max = valueMs;
            }
        }

        public double Percentile(double p)
        {
            lock (_sync)
            {
                if (_count == 0)
                    return 0;
                var target = (long)Math.Ceiling(_count * p / 100.0);
                long cumulative = 0;
                for (var i = 0; i < _counts.Length; i++)
                {
                    cumulative += _counts[i];
                    if (cumulative >= target)
                        return (i + 0.5) * _bucketMs;
                }
                return _max;
            }
        }

        public HistogramSummary ToSummary() => new HistogramSummary
        {
            Count = _count,
            MeanMs = _count == 0 ? 0 : _sum / _count,
            P50Ms = Percentile(50),
            P95Ms = Percentile(95),
            P99Ms = Percentile(99),
            MaxMs = _max,
        };

        public override string ToString()
        {
            var s = ToSummary();
            return $"n={s.Count} mean={s.MeanMs:F2} p50={s.P50Ms:F2} " +
                   $"p95={s.P95Ms:F2} p99={s.P99Ms:F2} max={s.MaxMs:F2}";
        }
    }

    private sealed class HistogramSummary
    {
        public long Count { get; set; }
        public double MeanMs { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public double MaxMs { get; set; }
    }
}
