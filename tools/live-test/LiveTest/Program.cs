using System.Text;
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
    private static string _requestedInstrument = "";
    private static string _listFilter = "";
    private static int _seconds = 45;
    private static bool _placeOrder;
    private static bool _resolveMode;

    private static FtdcMdAdapter _md;
    private static FtdcTdAdapter _td;
    private static int _reqId;
    private static int _mdTicks;
    private static int _mdSubAck;
    private static int _privateSeqNoCount;
    private static int _instrumentCount;
    private static int _positionCount;

    private static volatile bool _mdLoggedIn;
    private static volatile bool _settleDone;
    private static volatile bool _instrDone;
    private static volatile bool _accountDone;
    private static volatile bool _positionDone;
    private static volatile bool _orderSent;
    private static volatile bool _cancelSent;
    private static volatile bool _orderFinished;
    private static bool _noTickTimerStarted;

    private static volatile string _selectedInstrument;
    private static string _exchangeId;
    private static double _priceTick = 1;
    private static double _lastPrice;
    private static ThostFtdcRspUserLoginField _login;
    private static ThostFtdcInstrumentField _fallbackInstrument;
    private static string _orderRef = "";

    private static readonly object _orderLock = new();
    private static readonly TaskCompletionSource<bool> _mdReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource<bool> _tdReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource<bool> _orderDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: LiveTest --broker 9999 --user U --password P " +
                                    "--md-front tcp://ip:port --td-front tcp://ip:port " +
                                    "[--appid X --authcode Y] [--instrument rb2701] " +
                                    "[--seconds 45] [--place-order]");
            return 2;
        }

        var flowRoot = Path.Combine(Path.GetTempPath(), "ftdcnet-live-test",
            DateTime.Now.ToString("yyyyMMddHHmmss"));
        var mdFlow = Path.Combine(flowRoot, "md");
        var tdFlow = Path.Combine(flowRoot, "td");
        Directory.CreateDirectory(mdFlow);
        Directory.CreateDirectory(tdFlow);

        Log($"flow: {flowRoot}");
        Log($"MD front: {_mdFront}");
        Log($"TD front: {_tdFront}");
        if (!string.IsNullOrEmpty(_listFilter))
            Log($"list filter: {_listFilter} (CZCE)");

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

        if (!string.IsNullOrEmpty(_listFilter))
            _mdReady.TrySetResult(true);

        _md.RegisterFront(_mdFront);
        _td.RegisterFront(_tdFront);
        _md.Init();
        _td.Init();

        var all = Task.WhenAll(_mdReady.Task, _tdReady.Task);
        var timeout = Task.Delay(TimeSpan.FromSeconds(_seconds));
        var winner = await Task.WhenAny(all, timeout);
        if (winner == timeout)
        {
            Log($"RESULT: TIMEOUT after {_seconds}s " +
                $"(mdReady={_mdReady.Task.IsCompleted}, tdReady={_tdReady.Task.IsCompleted})");
            return 1;
        }

        var results = await all;
        var ok = results.All(x => x);
        Log($"RESULT: {(ok ? "OK" : "FAIL")} " +
            $"(mdTicks={_mdTicks}, instruments={_instrumentCount}, positions={_positionCount}, " +
            $"order={( _placeOrder ? (_orderFinished ? "done" : "not-finished") : "skipped")})");
        return ok ? 0 : 1;
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
            if (arg == "--resolve")
            {
                _resolveMode = true;
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
        _requestedInstrument = values.GetValueOrDefault("instrument", "");
        _listFilter = values.GetValueOrDefault("list", "");
        if (values.TryGetValue("seconds", out var seconds) &&
            int.TryParse(seconds, out var s) && s > 0)
            _seconds = s;
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
            Log($"[{name}] handler exception: {ex}");
        }
    }

    // ------------------------------------------------------------------ MD

    private static void MdOnFront(object sender, OnFrontEventArgs e) => Safe("MD", () =>
    {
        Log($"[MD] {e.EventType} reason={e.Reason}");
        if (e.EventType == EnumOnFrontType.OnFrontConnected)
            _md.ReqUserLogin(new ThostFtdcReqUserLoginField
            {
                BrokerID = _broker,
                UserID = _user,
                Password = _password,
            }, NextReqId());
    });

    private static void MdOnRsp(object sender, OnRspEventArgs e) => Safe("MD", () =>
    {
        Log($"[MD] {e.EventType} req={e.RequestID} last={e.IsLast} err={Err(e.RspInfo)}");
        switch (e.EventType)
        {
            case EnumOnRspType.OnRspUserLogin:
                if (!IsOk(e.RspInfo))
                {
                    _mdReady.TrySetResult(false);
                    return;
                }
                _mdLoggedIn = true;
                if (e.Param != IntPtr.Zero)
                {
                    var login = Conv.P2S<ThostFtdcRspUserLoginField>(e.Param);
                    Log($"[MD] login ok tradingDay={login.TradingDay} front={login.FrontID} session={login.SessionID}");
                }
                var mdInstrument = !string.IsNullOrEmpty(_selectedInstrument)
                    ? _selectedInstrument
                    : _requestedInstrument;
                if (!string.IsNullOrEmpty(mdInstrument))
                {
                    var ret = _md.SubscribeMarketData(new[] { mdInstrument });
                    Log($"[MD] SubscribeMarketData({mdInstrument}) -> {ret}");
                }
                if (!_resolveMode)
                {
                    _ = Task.Delay(10000).ContinueWith(_ =>
                    {
                        if (Volatile.Read(ref _mdSubAck) == 0)
                        {
                            Log("[MD] no subscription ack within 10s");
                            _mdReady.TrySetResult(true);
                        }
                    });
                }
                break;
            case EnumOnRspType.OnRspSubMarketData:
                var subId = "";
                if (e.Param != IntPtr.Zero)
                {
                    var f = Conv.P2S<ThostFtdcSpecificInstrumentField>(e.Param);
                    subId = f.InstrumentID;
                    Log($"[MD] subscribed {subId}");
                }
                Interlocked.Increment(ref _mdSubAck);
                if (!_resolveMode)
                    _mdReady.TrySetResult(true);
                // resolve mode: wait for an actual tick of the resolved contract
                break;
        }
    });

    private static void MdOnRtn(object sender, OnRtnEventArgs e) => Safe("MD", () =>
    {
        if (e.EventType != EnumOnRtnType.OnRtnDepthMarketData || e.Param == IntPtr.Zero)
            return;
        var tick = Conv.P2S<ThostFtdcDepthMarketDataField>(e.Param);
        var isSelected = !string.IsNullOrEmpty(_selectedInstrument) &&
                         string.Equals(tick.InstrumentID, _selectedInstrument, StringComparison.OrdinalIgnoreCase);
        if (_resolveMode && !isSelected)
            return; // ignore ticks for the not-yet-resolved/stale code
        var n = Interlocked.Increment(ref _mdTicks);
        if (n <= 5)
            Log($"[MD] tick {tick.InstrumentID} {tick.UpdateTime}.{tick.UpdateMillisec:D3} " +
                $"last={tick.LastPrice} vol={tick.Volume}");
        if (string.IsNullOrEmpty(_selectedInstrument) || isSelected)
            Volatile.Write(ref _lastPrice, tick.LastPrice);
        _mdReady.TrySetResult(true);
        TryPlaceOrder();
    });

    // ------------------------------------------------------------------ TD

    private static void TdOnFront(object sender, OnFrontEventArgs e) => Safe("TD", () =>
    {
        Log($"[TD] {e.EventType} reason={e.Reason}");
        if (e.EventType != EnumOnFrontType.OnFrontConnected)
            return;
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
            Log($"[TD] {e.EventType} ERROR req={e.RequestID} err={Err(e.RspInfo)}");
        else if (e.EventType != EnumOnRspType.OnRspQryInvestorPosition &&
                 e.EventType != EnumOnRspType.OnRspQryInstrument)
            Log($"[TD] {e.EventType} req={e.RequestID} last={e.IsLast} err={Err(e.RspInfo)}");

        switch (e.EventType)
        {
            case EnumOnRspType.OnRspAuthenticate:
                if (!IsOk(e.RspInfo))
                {
                    _tdReady.TrySetResult(false);
                    return;
                }
                Log("[TD] authenticate ok");
                ReqLogin();
                break;

            case EnumOnRspType.OnRspUserLogin:
                if (!IsOk(e.RspInfo))
                {
                    _tdReady.TrySetResult(false);
                    return;
                }
                if (e.Param != IntPtr.Zero)
                    _login = Conv.P2S<ThostFtdcRspUserLoginField>(e.Param);
                Log($"[TD] login ok tradingDay={_login?.TradingDay} front={_login?.FrontID} " +
                    $"session={_login?.SessionID} maxOrderRef={_login?.MaxOrderRef}");
                _td.ReqSettlementInfoConfirm(new ThostFtdcSettlementInfoConfirmField
                {
                    BrokerID = _broker,
                    InvestorID = _user,
                }, NextReqId());
                break;

            case EnumOnRspType.OnRspSettlementInfoConfirm:
                if (!IsOk(e.RspInfo))
                {
                    _tdReady.TrySetResult(false);
                    return;
                }
                Log("[TD] settlement confirmed");
                _settleDone = true;
                if (_resolveMode)
                {
                    StartResolver();
                    return;
                }
                _td.ReqQryInstrument(new ThostFtdcQryInstrumentField
                {
                    InstrumentID = string.IsNullOrEmpty(_listFilter) ? _requestedInstrument : "",
                    ExchangeID = string.IsNullOrEmpty(_listFilter) ? "" : "CZCE",
                }, NextReqId());
                break;

            case EnumOnRspType.OnRspQryInstrument:
                if (_resolveMode)
                    break;
                if (e.Param != IntPtr.Zero)
                    ConsiderInstrument(Conv.P2S<ThostFtdcInstrumentField>(e.Param));
                if (e.IsLast)
                {
                    if (!string.IsNullOrEmpty(_listFilter))
                    {
                        Log($"[TD] list '{_listFilter}' done: scanned={_instrumentCount}");
                        _tdReady.TrySetResult(true);
                        return;
                    }
                    _instrDone = true;
                    if (string.IsNullOrEmpty(_selectedInstrument) && _fallbackInstrument != null)
                        SelectInstrument(_fallbackInstrument);
                    Log($"[TD] instrument query done: count={_instrumentCount}, selected={_selectedInstrument}");
                    if (string.IsNullOrEmpty(_selectedInstrument))
                    {
                        Log("[TD] no trading instrument found");
                        _tdReady.TrySetResult(false);
                        return;
                    }
                    if (_mdLoggedIn)
                    {
                        var ret = _md.SubscribeMarketData(new[] { _selectedInstrument });
                        Log($"[MD] SubscribeMarketData({_selectedInstrument}) -> {ret}");
                    }
                    _td.ReqQryTradingAccount(new ThostFtdcQryTradingAccountField
                    {
                        BrokerID = _broker,
                        InvestorID = _user,
                    }, NextReqId());
                    TryPlaceOrder();
                }
                break;

            case EnumOnRspType.OnRspQryTradingAccount:
                if (e.Param != IntPtr.Zero)
                {
                    var account = Conv.P2S<ThostFtdcTradingAccountField>(e.Param);
                    Log($"[TD] account {account.AccountID} balance={account.Balance:F2} " +
                        $"available={account.Available:F2} margin={account.CurrMargin:F2}");
                }
                if (e.IsLast)
                {
                    _accountDone = true;
                    _td.ReqQryInvestorPosition(new ThostFtdcQryInvestorPositionField
                    {
                        BrokerID = _broker,
                        InvestorID = _user,
                    }, NextReqId());
                }
                break;

            case EnumOnRspType.OnRspQryInvestorPosition:
                if (e.Param != IntPtr.Zero)
                {
                    var pos = Conv.P2S<ThostFtdcInvestorPositionField>(e.Param);
                    Interlocked.Increment(ref _positionCount);
                    if (_positionCount <= 20)
                        Log($"[TD] position {pos.InstrumentID} {pos.PosiDirection} " +
                            $"pos={pos.Position} yd={pos.YdPosition} today={pos.TodayPosition} " +
                            $"margin={pos.UseMargin:F2}");
                }
                if (e.IsLast)
                {
                    Log($"[TD] position query done: count={_positionCount}");
                    _positionDone = true;
                    CheckTdReady();
                }
                break;

            case EnumOnRspType.OnRspOrderInsert:
                Log($"[TD] order insert rsp err={Err(e.RspInfo)}");
                if (!IsOk(e.RspInfo))
                    FinishOrder("order insert rejected");
                break;

            case EnumOnRspType.OnRspOrderAction:
                Log($"[TD] order action rsp err={Err(e.RspInfo)}");
                break;
        }
    });

    private static void ConsiderInstrument(ThostFtdcInstrumentField inst)
    {
        Interlocked.Increment(ref _instrumentCount);
        if (!string.IsNullOrEmpty(_listFilter) &&
            inst.InstrumentID.Contains(_listFilter, StringComparison.OrdinalIgnoreCase))
        {
            Log($"[TD] list {inst.InstrumentID} exchange={inst.ExchangeID} " +
                $"class={inst.ProductClass} trading={inst.IsTrading} expire={inst.ExpireDate}");
        }
        if (inst.IsTrading == 0 || string.IsNullOrEmpty(inst.InstrumentID))
            return;

        if (!string.IsNullOrEmpty(_requestedInstrument))
        {
            if (string.Equals(inst.InstrumentID, _requestedInstrument, StringComparison.OrdinalIgnoreCase))
                SelectInstrument(inst);
            return;
        }

        if (string.IsNullOrEmpty(_selectedInstrument) &&
            inst.ProductClass == EnumProductClassType.Futures &&
            (inst.InstrumentID.StartsWith("rb", StringComparison.OrdinalIgnoreCase) ||
             inst.InstrumentID.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ||
             inst.InstrumentID.StartsWith("cu", StringComparison.OrdinalIgnoreCase)))
        {
            SelectInstrument(inst);
            return;
        }
        if (inst.ProductClass == EnumProductClassType.Futures)
            _fallbackInstrument ??= inst;
    }

    private static void SelectInstrument(ThostFtdcInstrumentField inst)
    {
        _selectedInstrument = inst.InstrumentID;
        _exchangeId = inst.ExchangeID;
        _priceTick = inst.PriceTick > 0 ? inst.PriceTick : 1;
        Log($"[TD] selected instrument={_selectedInstrument} exchange={_exchangeId} " +
            $"priceTick={_priceTick} productClass={inst.ProductClass}");
    }

    private static void TdOnRtn(object sender, OnRtnEventArgs e) => Safe("TD", () =>
    {
        switch (e.EventType)
        {
            case EnumOnRtnType.OnRtnPrivateSeqNo:
                var seq = e.Param.ToInt32();
                if (Interlocked.Increment(ref _privateSeqNoCount) <= 10)
                    Log($"[TD] private seq no {seq}");
                break;

            case EnumOnRtnType.OnRtnOrder:
                if (e.Param == IntPtr.Zero) return;
                var order = Conv.P2S<ThostFtdcOrderField>(e.Param);
                if (!string.IsNullOrEmpty(_orderRef) && order.OrderRef != _orderRef)
                    return;
                Log($"[TD] order {order.OrderRef} {order.InstrumentID} " +
                    $"{order.OrderStatus} traded={order.VolumeTraded} msg={order.StatusMsg}");
                if (IsQueued(order.OrderStatus) && !_cancelSent)
                    CancelOrder(order);
                else if (order.OrderStatus == EnumOrderStatusType.Canceled ||
                         order.OrderStatus == EnumOrderStatusType.AllTraded ||
                         order.OrderStatus == EnumOrderStatusType.NoTradeNotQueueing)
                    FinishOrder($"final status {order.OrderStatus}");
                break;

            case EnumOnRtnType.OnRtnTrade:
                if (e.Param == IntPtr.Zero) return;
                var trade = Conv.P2S<ThostFtdcTradeField>(e.Param);
                Log($"[TD] trade {trade.InstrumentID} {trade.Direction} price={trade.Price} " +
                    $"volume={trade.Volume} tradeId={trade.TradeID}");
                FinishOrder("filled");
                break;

            case EnumOnRtnType.OnRtnInstrumentStatus:
                // too noisy; ignore unless debugging
                break;
        }
    });

    private static bool IsQueued(EnumOrderStatusType status) =>
        status == EnumOrderStatusType.NoTradeQueueing ||
        status == EnumOrderStatusType.PartTradedQueueing;

    private static void TdOnErrRtn(object sender, OnErrRtnEventArgs e) => Safe("TD", () =>
    {
        Log($"[TD] {e.EventType} err={Err(e.RspInfo)}");
        if (e.EventType == EnumOnErrRtnType.OnErrRtnOrderInsert ||
            e.EventType == EnumOnErrRtnType.OnErrRtnOrderAction)
            FinishOrder($"error callback {e.EventType}");
    });

    // ------------------------------------------------------------------ resolver

    private static void StartResolver()
    {
        var requested = _requestedInstrument;
        // Must run off the CTP callback thread: ResolveOnDemand/LoadAll block
        // while waiting for the query response that arrives on that thread.
        Task.Run(() =>
        {
            try
            {
                var resolver = new CzceInstrumentResolver(_td);
                if (string.IsNullOrEmpty(requested))
                {
                    var timeout = TimeSpan.FromSeconds(Math.Max(60, _seconds - 10));
                    var count = resolver.LoadAll(NextReqId(), timeout);
                    Log($"[RESOLVER] LoadAll count={count}, aliases={resolver.Aliases.Count}");
                    Log($"[RESOLVER] SA2705 -> {resolver.Resolve("SA2705") ?? "<not found>"}, " +
                        $"SA2709 -> {resolver.Resolve("SA2709") ?? "<not found>"}, " +
                        $"SO2707 -> {resolver.Resolve("SO2707") ?? "<not found>"}");
                    _mdReady.TrySetResult(true);
                    _tdReady.TrySetResult(count > 0);
                    return;
                }

                var resolveTimeout = TimeSpan.FromSeconds(Math.Max(15, _seconds / 2));
                var id = resolver.ResolveOnDemand(requested, NextReqId, resolveTimeout);
                Log($"[RESOLVER] {requested} -> {id ?? "<not found>"} (cache={resolver.Count})");
                if (string.IsNullOrEmpty(id))
                {
                    _tdReady.TrySetResult(false);
                    return;
                }

                _selectedInstrument = id;
                _exchangeId = "CZCE";
                if (_mdLoggedIn)
                {
                    var ret = _md.SubscribeMarketData(new[] { id });
                    Log($"[MD] SubscribeMarketData({id}) -> {ret}");
                    _ = Task.Delay(8000).ContinueWith(_ =>
                    {
                        if (Volatile.Read(ref _mdTicks) == 0)
                        {
                            Log("[MD] no tick after resolved subscription");
                            _mdReady.TrySetResult(true);
                        }
                    });
                }
                _tdReady.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Log($"[RESOLVER] {ex.GetType().Name}: {ex.Message}");
                _tdReady.TrySetResult(false);
            }
        });
    }

    // ------------------------------------------------------------------ order

    private static void TryPlaceOrder()
    {
        if (!_placeOrder)
            return;

        lock (_orderLock)
        {
            if (_orderSent || !_settleDone || !_instrDone ||
                string.IsNullOrEmpty(_selectedInstrument) || _login == null)
                return;

            var last = Volatile.Read(ref _lastPrice);
            if (last <= 0)
            {
                if (!_noTickTimerStarted)
                {
                    _noTickTimerStarted = true;
                    _ = Task.Delay(8000).ContinueWith(_ =>
                    {
                        if (Volatile.Read(ref _lastPrice) <= 0)
                            FinishOrder("no market data tick, order skipped");
                    });
                }
                return; // wait for a market-data tick
            }

            _orderSent = true;
            _orderRef = NextOrderRef(_login.MaxOrderRef);
            // 2% below last: inside exchange price limits, still unlikely to fill.
            var price = Math.Round(last * 0.98 / _priceTick) * _priceTick;
            if (price <= 0) price = _priceTick;

            var order = new ThostFtdcInputOrderField
            {
                BrokerID = _broker,
                InvestorID = _user,
                UserID = _user,
                InstrumentID = _selectedInstrument,
                ExchangeID = _exchangeId,
                OrderRef = _orderRef,
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

            Log($"[TD] place order ref={_orderRef} {_selectedInstrument} buy open " +
                $"price={price} (last={last})");
            var ret = _td.ReqOrderInsert(order, NextReqId());
            if (ret != 0)
            {
                Log($"[TD] ReqOrderInsert returned {ret}");
                FinishOrder("ReqOrderInsert failed");
            }
            else
            {
                _ = Task.Delay(12000).ContinueWith(_ => FinishOrder("order timeout"));
            }
        }
    }

    private static string NextOrderRef(string maxOrderRef)
    {
        if (long.TryParse(maxOrderRef, out var value))
            return (value + 1).ToString();
        return "1";
    }

    private static void CancelOrder(ThostFtdcOrderField order)
    {
        _cancelSent = true;
        var request = new ThostFtdcInputOrderActionField
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
        };
        Log($"[TD] cancel order ref={order.OrderRef}");
        var ret = _td.ReqOrderAction(request, NextReqId());
        if (ret != 0)
            FinishOrder($"ReqOrderAction returned {ret}");
    }

    private static void FinishOrder(string reason)
    {
        if (_orderFinished)
            return;
        _orderFinished = true;
        Log($"[TD] order test finished: {reason}");
        _orderDone.TrySetResult(true);
        CheckTdReady();
    }

    private static void CheckTdReady()
    {
        if (!_settleDone || !_instrDone || !_accountDone || !_positionDone)
            return;
        if (_placeOrder && !_orderDone.Task.IsCompleted)
            return;
        _tdReady.TrySetResult(true);
    }
}
