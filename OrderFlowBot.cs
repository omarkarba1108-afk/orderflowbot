#region Using declarations
using System; 
using NinjaTrader.Cbi;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Containers;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Events;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.DataBars;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies;
using NinjaTrader.Custom.AddOns.OrderFlowBot.States;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Gui;        // added
using NinjaTrader.Gui.Chart;  // added
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;

#endregion

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies
{
    public static class GroupConstants
    {
        public const string GROUP_NAME_GENERAL = "General";
        public const string GROUP_NAME_DATA_BAR = "Data Bar";
        public const string GROUP_NAME_TEST = "Backtest";
        public const string GROUP_NAME_ADVANCE = "Advance";
    }

    [Gui.CategoryOrder(GroupConstants.GROUP_NAME_GENERAL, 0)]
    [Gui.CategoryOrder(GroupConstants.GROUP_NAME_DATA_BAR, 1)]
    [Gui.CategoryOrder(GroupConstants.GROUP_NAME_TEST, 2)]
    [Gui.CategoryOrder(GroupConstants.GROUP_NAME_ADVANCE, 3)]
    public partial class OrderFlowBot : Strategy
    {
        private EventsContainer _eventsContainer;
        [SuppressMessage("SonarLint", "S4487", Justification = "Instantiated for event handling")]
        private ServicesContainer _servicesContainer;
        private EventManager _eventManager;
        private TradingEvents _tradingEvents;
        private StrategiesEvents _strategiesEvents;
        private DataBarEvents _dataBarEvents;

        private IReadOnlyDataBar _currentDataBar;
        private IReadOnlyTradingState _currentTradingState;

        // --- Time gate state ---
        private bool _validTimeRange;
        private int _parsedTimeStart;
        private int _parsedTimeEnd;

        private Dictionary<string, int> _dataSeriesIndexMap;

        // ===== Simple BE state =====
        private bool   _beArmed        = false;
        private bool   _movedToBE      = false;
        private double _beTriggerPrice = double.NaN;
        
        // Track current trade context for snapshots/BE
        private string _activeEntryTag   = null;
        private bool   _isLongEntry      = false;
        private double _entryPrice       = 0.0;
        private double _targetPrice      = double.NaN;
        private double _stopPrice        = double.NaN;  // last requested stop
        private int    _entryBarNumber   = -1;
        private int    _entryQty         = 0;
        
        // Exit tracking
        private double _lastFillPrice    = double.NaN;
        private string _lastExitReason   = "Unknown";

        // --- BE constants (tune here) ---
        private const double BE_FRACTION   = 0.70;  // 70% to target then BE (let winners run!)
        private const int    BE_PAD_TICKS  = 0;     // exactly at BE for scalps
        
        // --- Logging control ---
        private const bool LOG_ORDER_UPDATES = false;  // Silence order update spam
        private const bool LOG_MANAGED_ENTRY = false;  // Silence managed entry confirmation
        private const bool LOG_SAFEGUARD = false;      // Silence exception safeguards
        private const bool VERBOSE_HOST_LOGS = false;  // Silence misc host logs

        // referenced in OnStateChange / OnBarUpdate
        private OrderFlowCumulativeDelta _cumulativeDelta;

        private const int StrategyWarmupBars = 60;  // Reduced from 120 for faster trading

        #region General Properties
        [NinjaScriptProperty]
        [Display(Name = "Version", Description = "OrderFlowBot version.", Order = 0, GroupName = GroupConstants.GROUP_NAME_GENERAL)]
        [ReadOnly(true)]
        public string Version { get { return "3.0.0"; } }

        [NinjaScriptProperty]
        [Display(Name = "Daily Profit Enabled", Description = "Enable this to disable OFB after the daily realized profit is hit.", Order = 1, GroupName = GroupConstants.GROUP_NAME_GENERAL)]
        public bool DailyProfitEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Profit", Description = "The daily realized profit to disable OFB.", Order = 2, GroupName = GroupConstants.GROUP_NAME_GENERAL)]
        public double DailyProfit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Loss Enabled", Description = "Enable this to disable OFB after the daily realized loss is hit.", Order = 3, GroupName = GroupConstants.GROUP_NAME_GENERAL)]
        public bool DailyLossEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Loss", Description = "The daily realized loss to disable OFB.", Order = 4, GroupName = GroupConstants.GROUP_NAME_GENERAL)]
        public double DailyLoss { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Time Enabled", Description = "Enable this to enable time start/end.", Order = 5, GroupName = GroupConstants.GROUP_NAME_GENERAL)]
        public bool TimeEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Time Start", Description = "The allowed time to enable OFB.", Order = 6, GroupName = GroupConstants.GROUP_NAME_GENERAL)]
        public string TimeStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Time End", Description = "The allowed time to disable and close positions for OFB.", Order = 7, GroupName = GroupConstants.GROUP_NAME_GENERAL)]
        public string TimeEnd { get; set; }
        #endregion

        #region Data Bar Properties
        [NinjaScriptProperty]
        [Display(Name = "Ticks Per Level *", Description = "Set this to the same ticks per level that is being used.", Order = 0, GroupName = GroupConstants.GROUP_NAME_DATA_BAR)]
        public int TicksPerLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance Ratio", Description = "The minimum imbalance ratio.", Order = 1, GroupName = GroupConstants.GROUP_NAME_DATA_BAR)]
        public double ImbalanceRatio { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stacked Imbalance", Description = "The minimum number for a stacked imbalance.", Order = 2, GroupName = GroupConstants.GROUP_NAME_DATA_BAR)]
        public int StackedImbalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance Min Delta", Description = "The minimum number of delta between the bid and ask for a valid imbalance.", Order = 3, GroupName = GroupConstants.GROUP_NAME_DATA_BAR)]
        public long ImbalanceMinDelta { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Value Area Percentage", Description = "The percent to determine the value area.", Order = 4, GroupName = GroupConstants.GROUP_NAME_DATA_BAR)]
        public double ValueAreaPercentage { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cumulative Delta Period", Description = "The cumulative delta period.", Order = 5, GroupName = GroupConstants.GROUP_NAME_DATA_BAR)]
        [TypeConverter(typeof(CumulativeDeltaSelectedPeriodConverter))]
        public string CumulativeDeltaSelectedPeriod { get; set; }
        #endregion

        #region Backtest Properties
        [NinjaScriptProperty]
        [Display(Name = "Backtest Enabled", Description = "Enable this to backtest all strategies and directions.", Order = 0, GroupName = GroupConstants.GROUP_NAME_TEST)]
        public bool BacktestEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Backtest Strategy Name", Description = "The strategy name to backtest. This should be the same as the file name.", Order = 1, GroupName = GroupConstants.GROUP_NAME_TEST)]
        public string BacktestStrategyName { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Quantity", Description = "The name order quantity.", Order = 2, GroupName = GroupConstants.GROUP_NAME_TEST)]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Target", Description = "The target in ticks.", Order = 3, GroupName = GroupConstants.GROUP_NAME_TEST)]
        public int Target { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop", Description = "The stop in ticks.", Order = 4, GroupName = GroupConstants.GROUP_NAME_TEST)]
        public int Stop { get; set; }
        #endregion

        #region Advance Properties
        [NinjaScriptProperty]
        [Display(Name = "MarketEnvironment", Description = "This allows you to conditionally run different sections of your code for live or test.", Order = 0, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        [TypeConverter(typeof(MarketEnvironmentConverter))]
        public EnvironmentType MarketEnvironment { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "External Analysis Service Enabled", Description = "Enable this to allow requests to the external analysis service.", Order = 1, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        public bool ExternalAnalysisServiceEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "External Analysis Service HTTP", Description = "The external HTTP analysis service host.", Order = 2, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        public string ExternalAnalysisService { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Training Data Enabled", Description = "Enable this to write to a file used for training with backtest enabled.", Order = 3, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        public bool TrainingDataEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Training Data Directory", Description = "The directory to write the training data to.", Order = 4, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        public string TrainingDataDirectory { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Open Dashboard On Enable", Order = 5, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        public bool OpenDashboardOnEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dashboard Broadcast Enabled", Description = "Publish trade events to dashboard", Order = 6, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        public bool DashboardBroadcastEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Publish System Performance", Description = "Publish exact Ninja numbers from SystemPerformance", Order = 7, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        public bool PublishSystemPerformance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Mirror System Performance", Description = "Mirror SystemPerformance trades for exact Ninja matching", Order = 8, GroupName = GroupConstants.GROUP_NAME_ADVANCE)]
        public bool MirrorSystemPerformance { get; set; }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"An order flow trading bot";
                Name = "_OrderFlowBot";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 3;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;  // Silence platform order tracing
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = StrategyWarmupBars;
                IsInstantiatedOnEachOptimizationIteration = true;
                IncludeCommission = true;

                DailyProfitEnabled = false;
                DailyProfit = 1000;
                DailyLossEnabled = false;
                DailyLoss = 1000;
                TimeEnabled = true;
                TimeStart = "093000";
                TimeEnd = "160000";

                BacktestEnabled = false;
                BacktestStrategyName = "Stacked Imbalances";
                Target = 6;
                Stop = 4;
                Quantity = 1;

                TicksPerLevel = 1;
                ImbalanceRatio = 1.5;
                StackedImbalance = 3;
                ImbalanceMinDelta = 10;
                ValueAreaPercentage = 70;
                CumulativeDeltaSelectedPeriod = "Bar";

                MarketEnvironment = EnvironmentType.Live;
                ExternalAnalysisServiceEnabled = false;
                ExternalAnalysisService = "http://localhost:5000/analyze";
                TrainingDataEnabled = false;
                TrainingDataDirectory = "C://temp/";
                OpenDashboardOnEnable = true;
            }
            else if (State == State.Configure)
            {
                BarsRequiredToTrade = StrategyWarmupBars;   // enforce at configure time
                SetConfigs();
                SetMessagingConfigs();

                if (!ValidateTimeProperties())
                    return;

                _dataSeriesIndexMap = new Dictionary<string, int>
                {
                    { "Ema", 0 },
                    { "Atr", 3 }
                };

                AddDataSeries(BarsPeriodType.Tick,   1);
                AddDataSeries(BarsPeriodType.Minute, 1);   // BIP = 2
                AddDataSeries(BarsPeriodType.Tick, 2000);
            }
            else if (State == State.DataLoaded)
            {
                if (CumulativeDeltaSelectedPeriod == "Session")
                {
                    _cumulativeDelta = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Session, 0);
                }
                else
                {
                    _cumulativeDelta = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Bar, 0);
                }

                InitializeDataBar();
                InitializeTechnicalLevels();

                _eventsContainer = new EventsContainer();
                _servicesContainer = new ServicesContainer(_eventsContainer, new BacktestData
                {
                    Name = BacktestStrategyName,
                    IsBacktestEnabled = BacktestEnabled
                });

                _eventManager      = _eventsContainer.EventManager;
                _tradingEvents     = _eventsContainer.TradingEvents;
                _strategiesEvents  = _eventsContainer.StrategiesEvents;
                _dataBarEvents     = _eventsContainer.DataBarEvents;

                _eventsContainer.EventManager.OnPrintMessage += HandlePrintMessage;

                SetInitialDefaults();
                InitializeStrategyManager();

                if (Category != Category.Backtest)
                {
                    InitializeUIManager();
                }

         

                try
                {
                    if (ChartControl != null && ChartControl.Dispatcher != null)
                    {
                        ChartControl.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                        {
                            if (ChartControl == null) return;
                            var cmd = ApplicationCommands.NotACommand;
                            var keyBinding = new KeyBinding(cmd, Key.D, ModifierKeys.Control | ModifierKeys.Shift);
                          
                            ChartControl.InputBindings.Add(keyBinding);
                        }));
                    }
                }
                catch { }

                //if (!BacktestEnabled)
                //{
                //    CheckATMStrategyLoaded();
                //}
            }
            else if (State == State.Realtime && Category != Category.Backtest)
            {
                ReadyControlPanel();

                // If you disabled the time filter, force-enable trading/UI
                if (!TimeEnabled)
                {
                    _validTimeRange = true;
                    if (_servicesContainer != null)
                        _servicesContainer.TradingService.HandleEnabledDisabledTriggered(true);
                    if (_userInterfaceEvents != null)
                    {
                        _userInterfaceEvents.UpdateControlPanelLabel("OrderFlowBot");
                        _userInterfaceEvents.EnableAllControls();
                    }
                }
            }
            else if (State == State.Terminated && Category != Category.Backtest)
            {
                UnloadControlPanel();
            }
           
        }

        [SuppressMessage("SonarLint", "S125", Justification = "Commented code may be used later")]
        protected override void OnBarUpdate()
        {
            try
            {
                // ---------- TIME GATE (single source of truth) ----------
                if (TimeEnabled && BarsInProgress == 0)
                {
                    bool inWindow = ValidTimeRange();
                    if (inWindow != _validTimeRange)
                    {
                    _validTimeRange = inWindow;
                    UpdateValidStartEndTimeUserInterface(_validTimeRange);
                    }
                    if (!_validTimeRange)
                        return; // block entries on primary stream outside window
                }
                // ---------------------------------------------------------

                // Ensure defaults at the start of each session
                if (BarsInProgress == 0 && Bars != null && Bars.IsFirstBarOfSession)
                    SetInitialDefaults();

                // ---------- Unified warm-up & multi-series readiness ----------
                if (BarsInProgress == 0)
                {
                    if (CurrentBar < StrategyWarmupBars)
                        return;

                    if (BarsArray != null)
                    {
                        for (int i = 1; i < BarsArray.Length; i++)
                            if (CurrentBars[i] < 1)
                                return;
                    }
                }
                else
                {
                    if (CurrentBars[0] < StrategyWarmupBars || CurrentBars[BarsInProgress] < 1)
                        return;
                }
                // ----------------------------------------------------------------

                // Update lists once per new primary bar
                if (BarsInProgress == 0 && IsFirstTickOfBar)
                {
                    _eventsContainer.DataBarEvents.UpdateDataBarList();
                    _eventsContainer.TechnicalLevelsEvents.UpdateTechnicalLevelsList();
                }

                if (BarsInProgress == 0)
                {
                    _eventsContainer.TradingEvents.CurrentBarNumberTriggered(CurrentBars[0]);
                    _eventsContainer.DataBarEvents.UpdateCurrentDataBar(GetDataBarDataProvider(DataBarConfig.Instance));
                    _eventsContainer.TechnicalLevelsEvents.UpdateCurrentTechnicalLevels(GetTechnicalLevelsDataProvider());

                    // --- Managed entries + BE handling (single flow) ---
                    HandleManagedEntryIfAny();   // creates orders + arms BE state
                    CheckBreakevenSimple();      // simple BE at 65% to target
                }

                // UI / daily PnL / ATM checks (null-safe)
                if (!BacktestEnabled && _currentTradingState != null && _currentTradingState.IsTradingEnabled && _userInterfaceEvents != null)
                {
                    if (ValidDailyProfitLossHit())
                        UpdateDailyProfitLossUserInterface();

                    CheckAtmPosition();
                }
            }
            catch (Exception ex)
            {
                // Silenced: only print if safeguard logging enabled
                if (LOG_SAFEGUARD)
                {
                    Print("[SAFEGUARD] OnBarUpdate EX: " + ex.GetType().Name + ": " + ex.Message);
                    Print("[SAFEGUARD] BIP=" + BarsInProgress
                          + "  CB0=" + ((CurrentBars != null && CurrentBars.Length > 0) ? CurrentBars[0] : -1)
                          + "  CBN=" + CurrentBar);
                    Print("[SAFEGUARD] BarsArrayLen=" + (BarsArray != null ? BarsArray.Length : 0)
                          + "  TimeEnabled=" + TimeEnabled);
                }
            }
        }

        // Simple reliable BE - moves when price hits 65% to target
        private void CheckBreakevenSimple()
        {
            if (!_beArmed || _movedToBE || Position.MarketPosition == MarketPosition.Flat)
                return;

            double tick = Instrument.MasterInstrument.TickSize;
            if (tick <= 0) return;

            bool hit = _isLongEntry ? (GetCurrentBid() >= _beTriggerPrice)
                                    : (GetCurrentAsk() <= _beTriggerPrice);

            if (!hit) return;

            // Move stop to BE +/- pad ticks
            double be = _isLongEntry
                ? (Position.AveragePrice + BE_PAD_TICKS * tick)
                : (Position.AveragePrice - BE_PAD_TICKS * tick);

            be = Instrument.MasterInstrument.RoundToTickSize(be);

            // Issue the update (managed) — same entry signal tag
            SetStopLoss(_activeEntryTag, CalculationMode.Price, be, true);

            _stopPrice = be;
            _movedToBE = true;
        }

        protected override void OnOrderUpdate(
            NinjaTrader.Cbi.Order order,
            double limitPrice,
            double stopPrice,
            int quantity,
            int filled,
            double averageFillPrice,
            NinjaTrader.Cbi.OrderState orderState,
            DateTime time,
            NinjaTrader.Cbi.ErrorCode error,
            string nativeError)
        {
            // Hard-silence order update spam unless we want deep debug
            if (!LOG_ORDER_UPDATES) return;
            
            // (Optional) Debug only: Log rejections and errors
            if (orderState == NinjaTrader.Cbi.OrderState.Rejected || error != NinjaTrader.Cbi.ErrorCode.NoError)
            {
                Print(
                    "[OrderUpdate] Rejected: name=" + order.Name +
                    " from=" + order.FromEntrySignal +
                    " action=" + order.OrderAction +
                    " type=" + order.OrderType +
                    " state=" + orderState +
                    " limit=" + limitPrice.ToString("0.00") +
                    " stop=" + stopPrice.ToString("0.00") +
                    " qty=" + quantity +
                    " filled=" + filled +
                    " avgFill=" + averageFillPrice.ToString("0.00") +
                    " err=" + error +
                    " native='" + nativeError + "'"
                );
            }
        }

        // ---- Time-window helpers ----
        private static bool IsBetween(int t, int start, int end)
        {
            // Handles overnight windows like 230000 -> 063000
            if (start <= end) return t >= start && t <= end;
            return t >= start || t <= end;
        }

        private static double GetTickSize()
        {
            var cfg = DataBarConfig.Instance;
            if (cfg != null && cfg.TickSize > 0)
                return cfg.TickSize;
            return 0.25; // safe fallback
        }

        // Managed-entry path called once per primary bar
        private void HandleManagedEntryIfAny()
        {
            if (BarsInProgress != 0) return;

            var s = (_eventsContainer != null && _eventsContainer.StrategiesEvents != null)
                ? _eventsContainer.StrategiesEvents.StrategyData
                : null;
            if (s == null) return;

            // Only fresh Managed proposals
            if (s.ExecMode != EntryExecMode.Managed || !s.IsTriggered) return;
            if (!s.ProposedStopPrice.HasValue || !s.ProposedTargetPrice.HasValue) return;

            // One position at a time
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                s.ClearProposed();
                return;
            }

            // Tick size
            double tick = GetTickSize();

            // Round entry/stop/target to valid ticks
            double entry  = Instrument.MasterInstrument.RoundToTickSize(Close[0]);
            double stop   = Instrument.MasterInstrument.RoundToTickSize(s.ProposedStopPrice.Value);
            double target = Instrument.MasterInstrument.RoundToTickSize(s.ProposedTargetPrice.Value);

            bool isLong = (s.TriggeredDirection == Direction.Long);

            // Enforce side + ≥1-tick spacing
            if (isLong)
            {
                if (stop   >= entry) stop   = entry - tick;
                if (target <= entry) target = entry + tick;
            }
            else
            {
                if (stop   <= entry) stop   = entry + tick;
                if (target >= entry) target = entry - tick;
            }

            // Final safety
            if (Math.Abs(target - stop) < tick)
            {
                if (VERBOSE_HOST_LOGS)
                    Print("[Managed] Rejecting: target/stop too tight. stop=" + stop + " target=" + target);
                s.ClearProposed();
                return;
            }

            string tag = !string.IsNullOrEmpty(s.ProposedEntryTag) ? s.ProposedEntryTag
                              : (isLong ? "SIP_Long" : "SIP_Short");

            // Set exits BEFORE entry
            SetStopLoss(   tag, CalculationMode.Price, stop,   /*isSimulatedStop:*/ true);
            SetProfitTarget(tag, CalculationMode.Price, target);

            // Set context + arm BE
            _activeEntryTag = tag;
            _isLongEntry    = isLong;
            _entryPrice     = entry;
            _stopPrice      = stop;
            _targetPrice    = target;
            _entryQty       = Quantity;
            _entryBarNumber = CurrentBars[0];

            // Arm BE trigger (simple and robust)
            double dist = Math.Abs(_targetPrice - _entryPrice);
            _beTriggerPrice = _isLongEntry ? (_entryPrice + BE_FRACTION * dist)
                                           : (_entryPrice - BE_FRACTION * dist);
            _beArmed   = true;
            _movedToBE = false;

            // Print ENTRY snapshot (host-level, always prints)
            PrintEntrySnapshotHost(_isLongEntry, _entryPrice, _stopPrice, _targetPrice, _entryQty, _activeEntryTag);

            // Submit the entry (market)
            try
            {
                if (isLong) EnterLong(Quantity, tag);
                else        EnterShort(Quantity, tag);
            }
            catch (Exception ex)
            {
                if (VERBOSE_HOST_LOGS)
                    Print("[Managed] Submit error: " + ex.Message);
                s.ClearProposed();
                return;
            }

            // Consume so it’s not resent
            s.ClearProposed();
        }

        private void UpdateValidStartEndTimeUserInterface(bool validStartEndTime)
        {
            _servicesContainer.TradingService.HandleEnabledDisabledTriggered(validStartEndTime);

            if (_userInterfaceEvents != null)
            {
                if (validStartEndTime)
                {
                    _userInterfaceEvents.UpdateControlPanelLabel("OrderFlowBot");
                    _userInterfaceEvents.EnableAllControls();
                }
                else
                {
                    _userInterfaceEvents.UpdateControlPanelLabel("Invalid Time");
                    _userInterfaceEvents.DisableAllControls();
                }
            }
        }

        private bool ValidateTimeProperties()
        {
            // Accept 12:30, 12:30:00, 1230, 123000, etc.
            _parsedTimeStart = ParseFlexibleTimeToInt(TimeStart);
            _parsedTimeEnd   = ParseFlexibleTimeToInt(TimeEnd);

            if (_parsedTimeStart < 0 || _parsedTimeEnd < 0)
            {
                System.Windows.MessageBox.Show(
                    "Time Start/End must be valid times like 120000, 12:30, or 12:30:00.",
                    "Invalid Time Configuration",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning
                );

                if (_servicesContainer != null)
                    _servicesContainer.TradingService.HandleEnabledDisabledTriggered(false);

                return false;
            }

            return true;
        }

        private static int ParseFlexibleTimeToInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return -1;

            // keep digits only
            var digits = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (char.IsDigit(s[i])) digits.Append(s[i]);

            string d = digits.ToString();

            // normalize to HHMMSS
            if (d.Length == 1)        d = "0" + d + "0000"; // H -> 0H0000
            else if (d.Length == 2)   d = d + "0000";       // HH -> HH0000
            else if (d.Length == 3)   d = "0" + d + "00";   // HMM -> 0HMM00
            else if (d.Length == 4)   d = d + "00";         // HHMM -> HHMM00
            else if (d.Length == 5)   d = "0" + d;          // HMMSS -> 0HMMSS
            else if (d.Length != 6)   return -1;

            int hh = int.Parse(d.Substring(0, 2));
            int mm = int.Parse(d.Substring(2, 2));
            int ss = int.Parse(d.Substring(4, 2));
            if (hh < 0 || hh > 23 || mm < 0 || mm > 59 || ss < 0 || ss > 59) return -1;

            return int.Parse(d); // HHMMSS
        }

        #region Time Range and Profit/Loss
        private bool ValidDailyProfitLossHit()
        {
            if (Account == null) return false;

            double realizedProfitLoss = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            return (DailyProfitEnabled && realizedProfitLoss > DailyProfit)
                || (DailyLossEnabled && realizedProfitLoss < (DailyLoss * -1));
        }

        private void UpdateDailyProfitLossUserInterface()
        {
            _servicesContainer.TradingService.HandleEnabledDisabledTriggered(false);

            if (_userInterfaceEvents != null)
            {
                _userInterfaceEvents.UpdateControlPanelLabel("Profit/Loss Hit");
                _userInterfaceEvents.DisableAllControls();
            }
        }

        // Single source of truth for time window using the minute series (BIP=2) when available.
        private bool ValidTimeRange()
        {
            if (!TimeEnabled) return true;

            // Prefer minute series if live; fallback to primary
            if (BarsArray != null && BarsArray.Length > 2 &&
                CurrentBars != null && CurrentBars.Length > 2 && CurrentBars[2] >= 1)
            {
                int current = ToTime(Times[2][0]);
                return IsBetween(current, _parsedTimeStart, _parsedTimeEnd);
            }

            // Fallback: primary bar time
            int t = ToTime(Time[0]);
            return IsBetween(t, _parsedTimeStart, _parsedTimeEnd);
        }
        #endregion

        private void SetInitialDefaults()
        {
            _tradingEvents.ResetTriggeredTradingState();
            _eventsContainer.StrategiesEvents.ResetStrategyData();
            _validTimeRange = false;
        }

        private void SetMessagingConfigs()
        {
            MessagingConfig.Instance.MarketEnvironment = MarketEnvironment;
            MessagingConfig.Instance.ExternalAnalysisService = ExternalAnalysisService;
            MessagingConfig.Instance.ExternalAnalysisServiceEnabled = ExternalAnalysisServiceEnabled;
        }

        // ===== Snapshots (print only these two) =====
        private void PrintEntrySnapshotHost(bool isLong, double entry, double stop, double target, int qty, string tag)
        {
            Print("[FMS] " +
                string.Format("ENTRY_SNAPSHOT {0:yyyy-MM-dd HH:mm:ss} {1} trg={2:0.00} sl={3:0.00} tp={4:0.00} qty={5} tag={6}",
                    Time[0], (isLong ? "LONG" : "SHORT"), entry, stop, target, qty, tag));
        }

        private void PrintExitSnapshotHost(string reason, double realizedR, int barsInTrade)
        {
            Print("[FMS] " +
                string.Format("EXIT_SNAPSHOT {0:yyyy-MM-dd HH:mm:ss} reason={1} R={2:0.2} bars={3}",
                    Time[0], reason, realizedR, barsInTrade));
        }

        private void HandlePrintMessage(string eventMessage, bool addNewLine)
        {
            // Whitelist: Only show strategy snapshots we care about
                    bool isEntry = eventMessage.IndexOf("ENTRY_SNAPSHOT", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isExit  = eventMessage.IndexOf("EXIT_SNAPSHOT",  StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isEntry && !isExit) return;

                Print(eventMessage);
                if (addNewLine) Print("");
        }

        private void CheckATMStrategyLoaded()
        {
            string template = null;

            if (ChartControl != null)
            {
                var ownerChart = ChartControl.OwnerChart;
                if (ownerChart != null && ownerChart.ChartTrader != null && ownerChart.ChartTrader.AtmStrategy != null)
                    template = ownerChart.ChartTrader.AtmStrategy.Template;
            }

            if (template == null)
            {
                System.Windows.MessageBox.Show(
                    "ATM Strategy template is not loaded.",
                    "Alert",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning
                );
            }
        }
    }

    public class CumulativeDeltaSelectedPeriodConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context)
        {
            return true;
        }

        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context)
        {
            return true;
        }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            return new StandardValuesCollection(new[] { "Session", "Bar" });
        }
    }

    public class MarketEnvironmentConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context)
        {
            return true;
        }

        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context)
        {
            return true;
        }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            return new StandardValuesCollection(new[] { EnvironmentType.Live, EnvironmentType.Test });
        }
    }
}
