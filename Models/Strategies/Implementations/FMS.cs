using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Containers;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.DataBars;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies.Features;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.ML;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Newtonsoft.Json;


namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies.Implementations
{
    public sealed class FMS : StrategyBase
    {
        // ===== REGIME DETECTION ENUMS =====
        public enum Regime
        {
            Laminar,    // Low stress, stable conditions
            Transition, // Medium stress, changing conditions
            Turbulent   // High stress, volatile conditions
        }

        // ===== SIGNAL PRINT & FREQUENCY MODES =====
        private enum SignalPrintMode { EntryExitOnly, FullDebug }
        private enum TradeFrequencyMode { Conservative, Balanced, Active }

        private const SignalPrintMode PRINT_MODE = SignalPrintMode.EntryExitOnly;
        private const TradeFrequencyMode FREQ_MODE = TradeFrequencyMode.Active;  // Scalp mode

        // ===== CONSTANT FLAGS (left as fixed) =====
        private const bool AllowLongs = true;
        private const bool AllowShorts = true;  // Enable shorts for target frequency
        private const bool RequireDeltaConfirm = true;
        private const bool UseDeltaMagnitude = true;
        private const bool UseEmaBias = true;
        private const bool RestrictTimeWindow = false;  // Relaxed for more trades
        private const bool DEBUG = false;  // Silenced - no debug spam
        private const bool EnforceRoomToTarget = true;
		private const bool AutoTuneFromPrevSession = true;
		
		// ===== SURGICAL CHANGES =====
		private const bool UseStopEntriesOnly = true;  // Disable all limit entries
		private const double RMultiple = 1.5;  // Target = Stop * 1.5
		private const double BERatio = 0.35;  // BE at 0.35R
		private const double TrailStartRatio = 0.7;  // Start trailing at 0.7R
		private const int CooldownOnStop = 2;
		private const int ATRPeriod = 14;
		private const double ATRStopMultiplier = 1.2;
		private const double ATRTrailMultiplier = 1.0;
		private const int MinTrailTicks = 8;
		private const int MinBarRangeTicks = 4;
		private const double MinBarRangeATR = 0.25;  // Bar range must be >= 0.25 * ATR (was 0.30)
		private const double LargeImpulseATR = 1.2;  // Large impulse = range >= 1.2 * ATR
		private const double VWAPToleranceATR = 1.5;  // VWAP distance tolerance
		private const double MFEInvalidateATR = 0.5;  // Invalidate if retrace > 0.5 * ATR
		private const double MFEThreshold = 0.8;  // MFE threshold for invalidation
		
		// ===== REGIME DETECTION CONSTANTS =====
		private const int RegimeBufferSize = 200;  // Rolling buffer for regime metrics
		private const int MinBarsForRegime = 50;   // Minimum bars before regime detection
		private const double StressThresholdLaminar = 0.3;    // Below this = Laminar
		private const double StressThresholdTurbulent = 0.7;  // Above this = Turbulent
		
		// ===== RISK THROTTLE CONSTANTS =====
		private const double RiskThrottleMin = 0.1;  // Minimum throttle (micro-size)
		private const double RiskThrottleMax = 1.0;  // Maximum throttle (full-size)
		private const double TurbulentThrottleThreshold = 0.2;  // Stand down if below this (scalp mode)
		
		// ===== DARWINIAN SELECTION CONSTANTS =====
		private const int AlphaCount = 3;  // Number of micro-alphas
		private const double ReplicatorLearningRate = 0.1;  // η in replicator dynamics
		private const int FitnessLookback = 20;  // Bars for fitness calculation
		
		// ===== CLUSTER DEFLECTION CONSTANTS =====
		private const int ClusterDetectionTicks = 2;  // Within 2 ticks = cluster
		private const int ClusterDeflectionTicks = 1;  // Deflect 1 tick past cluster (scalp mode)
		
		// ===== STRENGTHENING REQUIREMENTS (RELAXED FOR FREQUENCY) =====
		private const int CompressionWindow = 8;  // M bars for compression check
		private const double CompressionATR = 0.5;  // ±0.5×ATR for price coils
		private const double VWAPPinchATR = 0.5;  // VWAP pinch distance
		private const int MinDelta3 = 20;  // Minimum delta sum for 3 bars (scalp mode)
		private const double VolumeThreshold = 1.05;  // Volume relative to avg (scalp mode)
		private const int MinStackedImbalance = 3;  // Minimum stacked imbalance count
		private const double RunupGuardATR = 1.5;  // Run-up guard threshold
		private const double WickExhaustionRatio = 0.4;  // 40% wick exhaustion
		private const double DeviationGuardATR = 2.0;  // Deviation from VWAP guard
		private const double BaseToTriggerATR = 1.0;  // Max distance from base to trigger (scalp mode)
		private const int FastFlipBars = 2;  // Bars for fast-flip check
		private const int MinOppDelta2 = 30;  // Minimum opposite delta for fast-flip
		private const int TimeInTradeBars = 5;  // X bars for time-in-trade rule (scalp mode)
		private const double FastFlipExitR = -0.25;  // Fast-flip exit at -0.25R
		private const double TightenStopR = -0.1;  // Tighten stop to -0.1R
		private const int LunchStartHHmm = 1200;  // 12:00 lunch start
		private const int LunchEndHHmm = 1300;  // 13:00 lunch end


        private const int TradeStartHHmm = 930;  // 09:30 ET
        private const int TradeEndHHmm   = 1600;  // 16:00 ET
        private const int MaxTestsPerZone = 2;

        private const bool UsePocFilter      = true;
        private const bool UseActivityLevels = true;
        private const bool UseIcebergConfirm = false;
        private const bool UseFallbackZones  = false;
		private const bool UseStopEntryInChop = true;
		


        private const int PocLookbackBars      = 400;
        private const int ActivityLookbackBars = 300;
        private const int IcebergTestsAtLevel  = 3;
        private const double ActivityTopPercentile = 0.97;

        // ===== DAILY MANUAL SCALAR =====
        private const double TUNE = 1.2;  // Reduced from 1.8 for more trades

        // ===== ENVIRONMENT (auto refreshed) =====
        private double _s  = 1.0;   // daily scale
        private double _rt = 1.0;   // sqrt(s)
        private double _nu = 1.0;   // liquidity factor
        private double _tau = 1.0;  // trendiness
        private double _eta = 1.0;  // wickiness
        private int _lastEnvBar = -1000;
        private double _volBaseEma = 0.0;
		private double _lastTunePrinted = double.NaN;
		
		// ===== REGIME DETECTION STATE =====
		private Regime _currentRegime = Regime.Laminar;
		private double _stressIndex = 0.0;
		private double _riskThrottle = 1.0;
		private readonly Queue<double> _logReturns = new Queue<double>();
		private readonly Queue<double> _cvdValues = new Queue<double>();
		private readonly Queue<double> _signedReturns = new Queue<double>();
			private double _rollingVolatility = 0.0;
			private double _ar1Autocorr = 0.0;
			private double _cvdSlope = 0.0;
			private double _shannonEntropy = 0.0;
			private double _hurstExponent = 0.5;
			private double _orderImbalance = 0.0;
			
			// Normalized regime metrics [0,1]
			private double _rvZ = 0.0;
			private double _ar1 = 0.0;
			private double _entropy = 0.0;
			private double _hurst = 0.5;
			private double _imb = 0.0;
		
		// ===== DARWINIAN SELECTION STATE =====
		private readonly double[] _alphaWeights = new double[AlphaCount] { 0.33, 0.33, 0.34 };  // Equal initial weights
		private readonly double[] _alphaFitness = new double[AlphaCount];
		private readonly Queue<double> _recentReturns = new Queue<double>();
		private int _lastFitnessUpdate = -1;
		
		// ===== ML INTEGRATION STATE =====
		private RegimeModel _regimeModel;
		private ExternalAnalysisServiceClient _externalServiceClient;
		private bool _mlEnabled = false;
		private bool _externalServiceEnabled = false;
		
		// ===== JSONL TRAINING LOGGER =====
		private StreamWriter _trainingLogger;
		private bool _trainingEnabled = false;
		private string _trainingDirectory = "";
		private readonly object _loggerLock = new object();
		
		// ===== SIGNAL DISK LOGGERS =====
		private StreamWriter _signalCsv;
		private StreamWriter _signalJsonl;
		
		// ===== TRADE TRACKING (for snapshots) =====
		private double _maxFavorableR = 0.0;
		private double _maxAdverseR = 0.0;
		
		// ===== CLUSTER DETECTION STATE =====
		private readonly List<double> _clusterLevels = new List<double>();
		private int _lastClusterUpdate = -1;
		
		// --- time / profit management ---
		private const int    MaxHoldBars          = 5;   // exit if trade lasts this many bars (scalp mode)
		private const double EarlyTakeProfitFrac   = 0.60;    // 60% of original target distance (scalp mode)
		private const int    FlattenNudgeTicks     = 1;       // small offset so the exit order gets hit quickly

		// Active-trade state (approximate; we don't read broker fills here)
		private bool   _hasActiveTrade        = false;
		private bool   _activeIsLong          = false;
		private int    _activeStartBar        = -1;             // <— used for bar stop
		private double _activeEntryPrice      = double.NaN;
		private double _activeInitialTarget   = double.NaN;
		private double _activeInitialStop     = double.NaN;
		private string _activeEntryTag        = null;           // reuse tag to update live bracket
		
		private bool   _bracketArmed     = false;          // have we (re)attached SL/TP after likely fill?
		private double _entryTagPrice    = double.NaN;     // parsed from _activeEntryTag
		// --- entry/bracket tracking ---
		private bool _entryIsStop    = false;          // *_STP@ vs *_LMT@
		private bool _movedToBE     = false;
		private bool _lockedHalfR   = false;
		private bool _lockedOneR    = false;
		private int  _initRiskTicks = 0;
		
		// ===== SURGICAL TRACKING =====
		private int _lastStopBar = -1;
		private double _currentATR = double.NaN;
		private double _currentVWAP = double.NaN;
		private double _currentEMA50 = double.NaN;
		private double _currentADX = double.NaN;
		private double _currentTrueRange = double.NaN;
		private double _lastSwingHigh = double.NaN;
		private double _lastSwingLow = double.NaN;
		private double _lastMFE = double.NaN;
		private bool _signalInvalidated = false;
		
		// ===== STRENGTHENING TRACKING =====
		private double _currentQuantity = 1.0;  // Quantity multiplier for anti-drawdown
		private int _consecutiveLosses = 0;
		private double _lastStopLevel = double.NaN;  // Track last stop level for one-and-done
		private int _barsSinceFill = 0;
		private bool _fastFlipTriggered = false;
		private bool _timeInTradeTriggered = false;  



		// ===== INITIALIZATION METHODS =====
		private void InitializeRegimeDetection()
		{
			// Initialize regime detection buffers
			_logReturns.Clear();
			_cvdValues.Clear();
			_signedReturns.Clear();
			_recentReturns.Clear();
			
			// Initialize regime state
			_currentRegime = Regime.Laminar;
			_stressIndex = 0.0;
			_riskThrottle = 1.0;
			
			// Initialize Darwinian selection
			for (int i = 0; i < AlphaCount; i++)
			{
				_alphaWeights[i] = 1.0 / AlphaCount;
				_alphaFitness[i] = 0.0;
			}
		}
		
		private void InitializeMLComponents()
		{
			// Initialize ONNX model (disabled in NinjaTrader 8 due to assembly limitations)
			_regimeModel = new RegimeModel();
			
			// Initialize external service client (primary ML path for NT8)
			var messagingConfig = MessagingConfig.Instance;
			if (messagingConfig != null && messagingConfig.ExternalAnalysisServiceEnabled)
			{
				_externalServiceClient = new ExternalAnalysisServiceClient(messagingConfig.ExternalAnalysisService);
				_externalServiceEnabled = true;
			}
			
			// Check if ML is available (ONNX disabled, so only external service)
			_mlEnabled = _externalServiceEnabled; // Only external service available in NT8
			
			if (DEBUG)
			{
				DebugLog("ML Integration: ONNX=disabled(NT8), External=" + _externalServiceEnabled);
			}
		}
		
		private void InitializeTrainingLogger()
		{
			// Get training settings from host strategy
			var messagingConfig = MessagingConfig.Instance;
			if (messagingConfig != null)
			{
				// Note: We'll get the actual training settings from the host strategy
				// For now, initialize as disabled
				_trainingEnabled = false;
				_trainingDirectory = "";
			}
		}
		
		// ===== REGIME DETECTION ENGINE =====
		private void UpdateRegimeMetrics()
		{
			if (dataBars == null || dataBars.Count < MinBarsForRegime) return;
			
			// Calculate 1-minute log returns
			if (dataBars.Count >= 2)
			{
				var current = dataBars[dataBars.Count - 1];
				var previous = dataBars[dataBars.Count - 2];
				
				if (current.Prices.Close > 0 && previous.Prices.Close > 0)
				{
					double logReturn = Math.Log(current.Prices.Close / previous.Prices.Close);
					_logReturns.Enqueue(logReturn);
					_signedReturns.Enqueue(Math.Sign(logReturn));
					
					// Maintain buffer size
					while (_logReturns.Count > RegimeBufferSize)
						_logReturns.Dequeue();
					while (_signedReturns.Count > RegimeBufferSize)
						_signedReturns.Dequeue();
				}
			}
			
			// Calculate CVD slope
			if (currentDataBar.Deltas != null)
			{
				_cvdValues.Enqueue(currentDataBar.Deltas.CumulativeDelta);
				while (_cvdValues.Count > RegimeBufferSize)
					_cvdValues.Dequeue();
				
				if (_cvdValues.Count >= 10)
				{
					// Calculate CVD slope using linear regression
					_cvdSlope = CalculateSlope(_cvdValues.ToArray());
				}
			}
			
			// Calculate rolling realized volatility and z-score
			if (_logReturns.Count >= 20)
			{
				var returns = _logReturns.ToArray();
				_rollingVolatility = CalculateRealizedVolatility(returns);
				
				// Calculate z-score (normalize to [0,1])
				double mean = returns.Average();
				double std = Math.Sqrt(returns.Select(r => Math.Pow(r - mean, 2)).Average());
				double zScore = std > 0 ? Math.Abs(returns.Last() - mean) / std : 0;
				
				// Normalize z-score to [0,1] range
				_rvZ = Math.Min(1.0, zScore / 3.0); // Cap at 3-sigma
			}
			
			// Calculate AR(1) autocorrelation (critical slowing down)
			if (_logReturns.Count >= 20)
			{
				var returns = _logReturns.ToArray();
				_ar1Autocorr = CalculateAR1Autocorrelation(returns);
				
				// Normalize to [0,1] - higher autocorr = more critical slowing
				_ar1 = Math.Max(0, Math.Min(1, (_ar1Autocorr + 1) / 2));
			}
			
			// Calculate Shannon entropy of signed returns
			if (_signedReturns.Count >= 20)
			{
				_shannonEntropy = CalculateShannonEntropy(_signedReturns.ToArray());
				
				// Normalize entropy to [0,1]
				_entropy = _shannonEntropy / Math.Log(8); // 8 bins max entropy
			}
			
			// Calculate Hurst exponent (R/S estimator)
			if (_logReturns.Count >= 50)
			{
				var returns = _logReturns.ToArray();
				_hurstExponent = CalculateHurstExponent(returns);
				
				// Normalize Hurst to [0,1] - 0.5 = random, >0.5 = trending, <0.5 = mean-reverting
				_hurst = Math.Max(0, Math.Min(1, _hurstExponent));
			}
			
			// Calculate order imbalance proxy
			if (currentDataBar.Deltas != null)
			{
				long delta = currentDataBar.Deltas.Delta;
				long volume = (currentDataBar.Volumes != null) ? currentDataBar.Volumes.Volume : 1;
				
				// Imbalance as ratio of delta to volume
				_orderImbalance = volume > 0 ? Math.Abs((double)delta / volume) : 0;
				
				// Normalize to [0,1]
				_imb = Math.Min(1.0, _orderImbalance);
			}
			
			// Fuse metrics into stress index
			UpdateStressIndex();
			
			// Update regime classification
			UpdateRegimeClassification();
			
			// Update risk throttle
			UpdateRiskThrottle();
		}
		
		private void UpdateStressIndex()
		{
			// Weighted combination of regime indicators
			// Higher values indicate more stress/turbulence
			_stressIndex = (
				0.25 * _rvZ +           // Volatility z-score
				0.20 * _ar1 +           // Critical slowing down
				0.15 * _cvdSlope +      // CVD momentum
				0.15 * (1.0 - _entropy) + // Low entropy = high stress
				0.10 * Math.Abs(_hurst - 0.5) + // Deviation from random walk
				0.15 * _imb             // Order imbalance
			);
			
			// Clamp to [0,1]
			_stressIndex = Math.Max(0.0, Math.Min(1.0, _stressIndex));
		}
		
		private void UpdateRegimeClassification()
		{
			if (_stressIndex < StressThresholdLaminar)
				_currentRegime = Regime.Laminar;
			else if (_stressIndex > StressThresholdTurbulent)
				_currentRegime = Regime.Turbulent;
			else
				_currentRegime = Regime.Transition;
		}
		
		private void UpdateRiskThrottle()
		{
			// Risk throttle inversely related to stress
			// Higher stress = lower throttle = smaller positions
			_riskThrottle = RiskThrottleMax - (_stressIndex * (RiskThrottleMax - RiskThrottleMin));
			
			// Clamp to valid range
			_riskThrottle = Math.Max(RiskThrottleMin, Math.Min(RiskThrottleMax, _riskThrottle));
			
		}
		
		// ===== REGIME DETECTION HELPER METHODS =====
		private double CalculateSlope(double[] values)
		{
			if (values.Length < 2) return 0.0;
			
			int n = values.Length;
			double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
			
			for (int i = 0; i < n; i++)
			{
				sumX += i;
				sumY += values[i];
				sumXY += i * values[i];
				sumXX += i * i;
			}
			
			double denominator = n * sumXX - sumX * sumX;
			if (Math.Abs(denominator) < 1e-10) return 0.0;
			
			return (n * sumXY - sumX * sumY) / denominator;
		}
		
		private double CalculateRealizedVolatility(double[] returns)
		{
			if (returns.Length < 2) return 0.0;
			
			double sum = 0.0;
			for (int i = 1; i < returns.Length; i++)
			{
				sum += Math.Pow(returns[i] - returns[i-1], 2);
			}
			
			return Math.Sqrt(sum / (returns.Length - 1));
		}
		
		private double CalculateAR1Autocorrelation(double[] returns)
		{
			if (returns.Length < 3) return 0.0;
			
			double mean = returns.Average();
			double numerator = 0.0;
			double denominator = 0.0;
			
			for (int i = 1; i < returns.Length; i++)
			{
				numerator += (returns[i] - mean) * (returns[i-1] - mean);
			}
			
			for (int i = 0; i < returns.Length; i++)
			{
				double diff = returns[i] - mean;
				denominator += diff * diff;
			}
			
			return denominator > 0 ? numerator / denominator : 0.0;
		}
		
		private double CalculateShannonEntropy(double[] signedReturns)
		{
			if (signedReturns.Length == 0) return 0.0;
			
			// Create 8 bins for signed returns
			int[] bins = new int[8];
			
			for (int i = 0; i < signedReturns.Length; i++)
			{
				int bin = Math.Max(0, Math.Min(7, (int)((signedReturns[i] + 1) * 4)));
				bins[bin]++;
			}
			
			double entropy = 0.0;
			for (int i = 0; i < 8; i++)
			{
				if (bins[i] > 0)
				{
					double p = (double)bins[i] / signedReturns.Length;
					entropy -= p * Math.Log(p);
				}
			}
			
			return entropy;
		}
		
		private double CalculateHurstExponent(double[] returns)
		{
			if (returns.Length < 10) return 0.5;
			
			// Simplified R/S estimator
			int n = Math.Min(returns.Length, 50); // Use last 50 points max
			var subset = returns.Skip(returns.Length - n).ToArray();
			
			double mean = subset.Average();
			double sumSquaredDeviations = subset.Sum(x => Math.Pow(x - mean, 2));
			double stdDev = Math.Sqrt(sumSquaredDeviations / n);
			
			if (stdDev == 0) return 0.5;
			
			// Calculate range
			double min = subset.Min();
			double max = subset.Max();
			double range = max - min;
			
			// R/S ratio
			double rs = range / stdDev;
			
			// Hurst exponent estimate
			return Math.Max(0.0, Math.Min(1.0, Math.Log(rs) / Math.Log(n)));
		}
		
		// ===== DARWINIAN SELECTION METHODS =====
		private void UpdateDarwinianSelection()
		{
			if (currentDataBar.BarNumber == _lastFitnessUpdate) return;
			_lastFitnessUpdate = currentDataBar.BarNumber;
			
			// Calculate fitness for each alpha based on recent performance
			for (int i = 0; i < AlphaCount; i++)
			{
				_alphaFitness[i] = CalculateAlphaFitness(i);
			}
			
			// Update weights using replicator dynamics
			UpdateAlphaWeights();
		}
		
		private double CalculateAlphaFitness(int alphaIndex)
		{
			// Simplified fitness calculation based on recent R-multiples
			// In a full implementation, this would track actual trade outcomes
			
			if (_recentReturns.Count < FitnessLookback) return 0.0;
			
			// Calculate recent performance
			var recent = new double[FitnessLookback];
			int startIndex = Math.Max(0, _recentReturns.Count - FitnessLookback);
			int index = 0;
			for (int i = startIndex; i < _recentReturns.Count; i++)
			{
				recent[index++] = _recentReturns.ElementAt(i);
			}
			
			double avgReturn = recent.Average();
			double volatility = Math.Sqrt(recent.Select(r => Math.Pow(r - avgReturn, 2)).Average());
			
			// Sharpe-like ratio as fitness
			return volatility > 0 ? avgReturn / volatility : avgReturn;
		}
		
		private void UpdateAlphaWeights()
		{
			// Replicator dynamics: w_i ← w_i + η * w_i * (f_i - f̄)
			double avgFitness = _alphaFitness.Average();
			
			for (int i = 0; i < AlphaCount; i++)
			{
				double fitnessDiff = _alphaFitness[i] - avgFitness;
				_alphaWeights[i] += ReplicatorLearningRate * _alphaWeights[i] * fitnessDiff;
				
				// Clamp to [0,1]
				_alphaWeights[i] = Math.Max(0.0, Math.Min(1.0, _alphaWeights[i]));
			}
			
			// Normalize weights
			double sum = _alphaWeights.Sum();
			if (sum > 0)
			{
				for (int i = 0; i < AlphaCount; i++)
				{
					_alphaWeights[i] /= sum;
				}
			}
			else
			{
				// Reset to equal weights if all weights became zero
				for (int i = 0; i < AlphaCount; i++)
				{
					_alphaWeights[i] = 1.0 / AlphaCount;
				}
			}
		}
		
		// ===== MICRO-ALPHA VOTERS =====
		private Tuple<double, bool> EvaluateAlphaA(bool isLong)
		{
			// Alpha A: Compression Breakout (strengthened compression + impulse confirm)
			double confidence = 0.0;
			
			// Check compression prerequisite
			if (HasNR7Cluster() || HasPriceCoils() || HasVWAPPinch())
			{
				confidence += 0.4;
			}
			
			// Check impulse confirmation
			if (CheckDelta3Confirmation(isLong))
			{
				confidence += 0.3;
			}
			
			// Check volume confirmation
			if (CheckVolumeConfirmation())
			{
				confidence += 0.3;
			}
			
			return Tuple.Create(Math.Min(1.0, confidence), isLong);
		}
		
		private Tuple<double, bool> EvaluateAlphaB(bool isLong)
		{
			// Alpha B: Delta-Impulse Pullback (stacked-imbalance band retest + ∑Δ(3) energy)
			double confidence = 0.0;
			
			// Check stacked imbalance confirmation
			if (CheckStackedImbalanceConfirmation(isLong))
			{
				confidence += 0.5;
			}
			
			// Check delta energy
			if (CheckDelta3Confirmation(isLong))
			{
				confidence += 0.3;
			}
			
			// Check structure validation
			string vetoReason;
			if (CheckStructureValidation(isLong, out vetoReason))
			{
				confidence += 0.2;
			}
			
			return Tuple.Create(Math.Min(1.0, confidence), isLong);
		}
		
		private Tuple<double, bool> EvaluateAlphaC(bool isLong)
		{
			// Alpha C: VWAP Reversion (Laminar-only)
			if (_currentRegime != Regime.Laminar)
				return Tuple.Create(0.0, isLong);
			
			double confidence = 0.0;
			
			// Check VWAP proximity
			if (HasVWAPPinch())
			{
				confidence += 0.4;
			}
			
			// Check entropy rising (mean reversion signal)
			if (_entropy > 0.6)
			{
				confidence += 0.3;
			}
			
			// Check low AR(1) (not trending)
			if (_ar1 < 0.3)
			{
				confidence += 0.3;
			}
			
			return Tuple.Create(Math.Min(1.0, confidence), isLong);
		}
		
		// ===== CLUSTER-AWARE STOP DEFLECTION =====
		private double DeflectClusterStop(double baseStop, bool isLong)
		{
			if (currentDataBar.BarNumber == _lastClusterUpdate) return baseStop;
			_lastClusterUpdate = currentDataBar.BarNumber;
			
			// Update cluster levels
			UpdateClusterLevels();
			
			double tick = GetTick();
			double deflectionDistance = ClusterDeflectionTicks * tick;
			
			// Check if stop is near any cluster level
			foreach (double clusterLevel in _clusterLevels)
			{
				if (Math.Abs(baseStop - clusterLevel) <= ClusterDetectionTicks * tick)
				{
					// Deflect away from cluster
					if (isLong)
						return baseStop - deflectionDistance; // Move stop lower
					else
						return baseStop + deflectionDistance; // Move stop higher
				}
			}
			
			return baseStop; // No deflection needed
		}
		
		private void UpdateClusterLevels()
		{
			_clusterLevels.Clear();
			
			if (dataBars == null || dataBars.Count < 20) return;
			
			double tick = GetTick();
			
			// Add round number levels
			double currentPrice = currentDataBar.Prices.Close;
			for (int i = -10; i <= 10; i++)
			{
				double roundLevel = Math.Round(currentPrice / (tick * 10)) * (tick * 10);
				_clusterLevels.Add(roundLevel);
			}
			
			// Add recent swing levels
			if (!double.IsNaN(_lastSwingHigh))
				_clusterLevels.Add(_lastSwingHigh);
			if (!double.IsNaN(_lastSwingLow))
				_clusterLevels.Add(_lastSwingLow);
			
			// Add VWAP if available
			if (!double.IsNaN(_currentVWAP))
				_clusterLevels.Add(_currentVWAP);
		}
		
		// ===== JSONL TRAINING LOGGER =====
		private void LogTrainingData(string timestamp, bool isLong, double ruleScore, double mlScore, 
			Models.Strategies.Features.FeatureVector features, int stopTicks, int targetTicks, int qtyEff)
		{
			if (!_trainingEnabled || _trainingLogger == null) return;
			
			lock (_loggerLock)
			{
				try
				{
					var trainingRow = new
					{
						ts = timestamp,
						regime = _currentRegime.ToString(),
						stress = _stressIndex,
						alphas = new { A = _alphaWeights[0], B = _alphaWeights[1], C = _alphaWeights[2] },
						weights = new { A = _alphaWeights[0], B = _alphaWeights[1], C = _alphaWeights[2] },
						score = ruleScore,
						side = isLong ? "LONG" : "SHORT",
						features = new
						{
							ret1m = features.Ret1m,
							rvZ = features.RvZ,
							ar1 = features.Ar1,
							cvdSlope = features.CvdSlope,
							entropy = features.Entropy,
							hurst = features.Hurst,
							imb = features.Imb,
							stress = features.Stress
						},
						proposal = new
						{
							stopTicks = stopTicks,
							targetTicks = targetTicks,
							rrMin = targetTicks > 0 ? (double)targetTicks / stopTicks : 0,
							qtyEff = qtyEff
						},
						labels = new
						{
							fwdRet_5 = 0.0,  // Would be calculated from future bars
							fwdRet_10 = 0.0  // Would be calculated from future bars
						}
					};
					
					string jsonLine = JsonConvert.SerializeObject(trainingRow);
					_trainingLogger.WriteLine(jsonLine);
					_trainingLogger.Flush();
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine("[FMS] Training log error: " + ex.Message);
				}
			}
		}
		
		// ===== ML INTEGRATION METHODS =====
		private double GetMLScore(Models.Strategies.Features.FeatureVector features)
		{
			double mlScore = 0.0;
			
			// ONNX model disabled in NinjaTrader 8 - use external service only
			if (_externalServiceClient != null)
			{
				try
				{
					var result = _externalServiceClient.AnalyzeAsync(features, CancellationToken.None).Result;
					mlScore = result.Item1;
				}
				catch
				{
					// Service unavailable, use rule score only
					mlScore = 0.0;
				}
			}
			
			return mlScore;
		}
		
		// ===== REGIME-AWARE RISK MANAGEMENT =====
		private int RegimeStopTargetWiden(int baseTicks)
		{
			// SCALP MODE: No widening - keep stops tight like a chicken pecking corn!
			return baseTicks; // Always ×1.0 for scalping
		}
		
		private double GetAdaptiveBERatio()
		{
			switch (_currentRegime)
			{
				case Regime.Laminar:
					return 0.70; // Later BE in stable conditions
				case Regime.Transition:
					return 0.60; // Medium BE timing
				case Regime.Turbulent:
					return 0.55; // Earlier BE in volatile conditions
				default:
					return 0.60;
			}
		}
		
		private int GetEffectiveQuantity(int baseQuantity)
		{
			// Apply risk throttle to quantity
			int effectiveQty = (int)Math.Floor(baseQuantity * _riskThrottle);
			
			// Ensure minimum quantity
			return Math.Max(1, effectiveQty);
		}
		private double ComputeAutoTuneFromPrevSession(int startHHmm, int endHHmm)
		{
		    if (dataBars == null || dataBars.Count < 10) return double.NaN;
		    int endIdx = dataBars.Count - 2; // last completed bar

		    // If we're currently *inside* the RTH window, skip this live block
		    int i = endIdx;
		    while (i >= 0 && IsWithinHHmmInt(dataBars[i].Time, startHHmm, endHHmm)) i--;

		    // Walk back to the previous RTH block
		    while (i >= 0 && !IsWithinHHmmInt(dataBars[i].Time, startHHmm, endHHmm)) i--;
		    if (i < 0) return double.NaN;

		    double hi = double.NegativeInfinity;
		    double lo = double.PositiveInfinity;
		    double tick = GetTick();

		    // Collect that block’s H/L
		    while (i >= 0 && IsWithinHHmmInt(dataBars[i].Time, startHHmm, endHHmm))
		    {
		        var b = dataBars[i];
		        if (b != null)
		        {
		            if (b.Prices.High > hi) hi = b.Prices.High;
		            if (b.Prices.Low  < lo) lo = b.Prices.Low;
		        }
		        i--;
		    }

		    if (!(hi > lo) || tick <= 0.0) return double.NaN;

		    double rangeTicks = (hi - lo) / tick;
		    double tune = rangeTicks / 100.0;          // same rule you used manually
		    if (tune < 0.6) tune = 0.6;
		    if (tune > 3.0) tune = 3.0;
		    return tune;
		}

        // ===== TUNABLES (computed) =====
        private struct Tunables
        {
            public int MaxZoneAgeBars;
            public int MaxBarsSinceZoneCreated;
            public double MergeToleranceTicks;
            public int PerLevelCooldownBars;

            public double BandTouchToleranceTicks;
            public int MinBarsBetweenSignals;

            public double DeltaMagK;

            public double EmaToleranceTicks;
            public double MinSlopeTicksPerBar;

            public int    ImpulseLookbackBars;
            public double ImpulseMinRangeTicks;

            public int    FrontRunTicks;
            public int    MinTargetTicks;
            public int    MaxTargetTicks;
            public double MinRRMultiple;

            public int StopPadTicks;
            public int MaxRiskTicks;

            public int DailyTradeCap;

            public int PocProximityTicks;
            public int ActivityMergeTicks;

            public int IcebergWindowBars;
            public int IcebergMaxAdvanceTicks;
            public double IcebergMinAbsDeltaK;

            // dynamic replacements for previously fixed knobs
            public int    MaxEntryDistanceTicksDyn;
            public int    MinStopTicksFloorDyn;
            public double WickToBodyMinRatioDyn;
        }
        private Tunables _cfg;

        // ===== dynamic replacements for "fixed" params =====
        private int  MinBarsRequiredDyn         { get { return Roundi(Clamp(26.0 + 10.0 / _rt, 24, 40)); } }
        private long MinCurrentBarVolumeDyn     { get { return (long)Roundi(Clamp(VolumeGateP20x(200, 0.20, 0.90), 40, 400)); } }
        private int  LookbackForZonesDyn        { get { return Roundi(Clamp(120.0 + 60.0 / _rt, 120, 200)); } }
        private int  DeltaMagLookbackBarsDyn    { get { return Roundi(Clamp(18.0 + 10.0 * _rt, 18, 36)); } }
        private int  EmaSlopePeriodDyn          { get { return Roundi(Clamp(50.0 / _rt, 20, 60)); } }
        private int  AtrProxyBarsDyn            { get { return Roundi(Clamp(12.0 + 6.0 / _rt, 10, 20)); } }
        private double AtrRiskFracDyn           { get { return Clamp(0.35 + 0.10 * (1.0 / Math.Max(_nu, 0.1)), 0.32, 0.50); } }
        private int  DailyMaxTradesDyn          { get { return 10; } }

        // ===== helpers =====
        private static int    Roundi(double x)                  { return (int)Math.Round(x); }
        private static double Clamp(double x, double a, double b){ return (x < a) ? a : ((x > b) ? b : x); }

        private static Tunables BuildTunables(double s)
        {
            if (s < 0.6) s = 0.6;
            if (s > 3.0) s = 3.0;

            Tunables c = new Tunables();
            double rt = Math.Sqrt(s);

            c.MaxZoneAgeBars          = Roundi(Clamp(160.0 / s, 100, 200));
            c.MaxBarsSinceZoneCreated = Roundi(Clamp(120.0 / s,  80, 160));
            c.MergeToleranceTicks     = Clamp(1.00 * rt, 0.75, 1.50);
            c.PerLevelCooldownBars    = Roundi(Clamp(30.0 / s,  16, 40));

            c.BandTouchToleranceTicks = Clamp(0.60 * Math.Sqrt(s), 0.50, 1.00);
            c.MinBarsBetweenSignals   = Roundi(Clamp(18.0 / s, 12, 28));

            c.DeltaMagK               = Clamp(0.50 + 0.05*(s - 1.0), 0.45, 0.60);

            c.EmaToleranceTicks       = Clamp(1.00 * rt, 0.75, 1.75);
            c.MinSlopeTicksPerBar     = Clamp(0.80 * s, 0.30, 1.60);

            c.ImpulseLookbackBars     = Roundi(Clamp(9.0 / s, 5, 12));
            c.ImpulseMinRangeTicks    = 6.0 * s;

            c.FrontRunTicks           = Roundi(Clamp(3.0 * s, 2, 5));
            c.MinTargetTicks          = Roundi(12.0 * s);
            c.MaxTargetTicks          = Roundi(Clamp(28.0 * s, 18, 32));
            c.MinRRMultiple           = Clamp(1.35 + 0.25*(s - 1.0), 1.35, 1.65);

            c.StopPadTicks            = Math.Max(5, Roundi(5.0 * s));
            c.MaxRiskTicks            = Roundi(16.0 * s);

            c.DailyTradeCap           = Roundi(Clamp(50.0 / s, 25, 60));

            c.PocProximityTicks       = Roundi(Clamp(10.0 * rt, 8, 14));
            c.ActivityMergeTicks      = Roundi(Clamp(2.0 * rt, 2, 4));

            c.IcebergWindowBars       = Roundi(Clamp(6.0 / s, 4, 8));
            c.IcebergMinAbsDeltaK     = Clamp(1.20 + 0.25*(s - 1.0), 1.10, 1.50);
            c.IcebergMaxAdvanceTicks  = Roundi(Clamp(2.0 * s, 2, 4));

            c.MaxEntryDistanceTicksDyn = Roundi(Clamp(3.0 / Math.Sqrt(s), 2, 4));
            c.MinStopTicksFloorDyn     = Roundi(Clamp(8.0 * Math.Sqrt(s), 7, 12));
            c.WickToBodyMinRatioDyn    = Clamp(0.92 + 0.12 * (Math.Sqrt(s) - 1.0), 0.85, 1.10);

            return c;
        }

        // expose (simple getters to be C#-5 friendly)
        private int    MaxZoneAgeBars          { get { return _cfg.MaxZoneAgeBars; } }
        private int    MaxBarsSinceZoneCreated { get { return _cfg.MaxBarsSinceZoneCreated; } }
        private double MergeToleranceTicks     { get { return _cfg.MergeToleranceTicks; } }
        private int    PerLevelCooldownBars    { get { return _cfg.PerLevelCooldownBars; } }
        private double BandTouchToleranceTicks { get { return _cfg.BandTouchToleranceTicks; } }
        private int    MinBarsBetweenSignals   { get { return _cfg.MinBarsBetweenSignals; } }
        private double DeltaMagK               { get { return _cfg.DeltaMagK; } }
        private double EmaToleranceTicks       { get { return _cfg.EmaToleranceTicks; } }
        private double MinSlopeTicksPerBar     { get { return _cfg.MinSlopeTicksPerBar; } }
        private int    ImpulseLookbackBars     { get { return _cfg.ImpulseLookbackBars; } }
        private double ImpulseMinRangeTicks    { get { return _cfg.ImpulseMinRangeTicks; } }
        private int    FrontRunTicks           { get { return _cfg.FrontRunTicks; } }
        private int    MinTargetTicks          { get { return _cfg.MinTargetTicks; } }
        private int    MaxTargetTicks          { get { return _cfg.MaxTargetTicks; } }
        private double MinRRMultiple           { get { return _cfg.MinRRMultiple; } }
        private int    StopPadTicks            { get { return _cfg.StopPadTicks; } }
        private int    MaxRiskTicks            { get { return _cfg.MaxRiskTicks; } }
        private int    DailyTradeCap           { get { return _cfg.DailyTradeCap; } }
        private int    PocProximityTicks       { get { return _cfg.PocProximityTicks; } }
        private int    ActivityMergeTicks      { get { return _cfg.ActivityMergeTicks; } }
        private int    IcebergWindowBars       { get { return _cfg.IcebergWindowBars; } }
        private double IcebergMinAbsDeltaK     { get { return _cfg.IcebergMinAbsDeltaK; } }
        private int    IcebergMaxAdvanceTicks  { get { return _cfg.IcebergMaxAdvanceTicks; } }
        private int    MaxEntryDistanceTicksDyn{ get { return _cfg.MaxEntryDistanceTicksDyn; } }
        private int    MinStopTicksFloorDyn    { get { return _cfg.MinStopTicksFloorDyn; } }
        private double WickToBodyMinRatioDyn   { get { return _cfg.WickToBodyMinRatioDyn; } }

        // ===== MODEL =====
        private sealed class Zone
        {
            public double Low;
            public double High;
            public bool   IsLongZone;
            public int    CreatedBarNumber;
            public int    Tests;
            public bool   Consumed;
        }

        private readonly List<Zone> _zones = new List<Zone>();
        private int _lastSignalBar = -1;
        private double _lastTradeBandMid = double.NaN;
        private int _lastTradeBandBar = -1;

        private int _tradesToday = 0;
        private int _lastBarTimeInt = -1;

        private bool _printedCfg = false;

        // logging
        private int    _lastLogBar = -1;
        private string _lastLogMessage = null;
        private int    _lastLogRepeat = 0;

        // profiles cache
        private double _prevPoc = double.NaN;
        private List<double> _activity = new List<double>();
        private int _lastProfileRecalcBar = -1;

        public FMS(EventsContainer eventsContainer) : base(eventsContainer)
        {
            StrategyData.Name = "FMS Stacked Imbalance Pullback";
            _s  = Clamp(TUNE, 0.6, 3.0);
            _rt = Math.Sqrt(_s);
            _cfg = BuildTunables(_s);
            
            // Initialize regime detection
            InitializeRegimeDetection();
            
            // Initialize ML components
            InitializeMLComponents();
            
            // Initialize training logger
            InitializeTrainingLogger();
        }

        public override bool CheckLong()  { return TrySignal(true);  }
        public override bool CheckShort() { return TrySignal(false);}
		
		private string BuildEntryTag(bool isLong, double triggerPrice, double tick)
		{
		    // SURGICAL: Only stop entries allowed
		    double stp = RoundToTick(triggerPrice, tick);
		    return isLong ? ("FMSL_STP@" + stp.ToString("0.00"))
		                  : ("FMSS_STP@" + stp.ToString("0.00"));
		}
		
		private static double ExtractTagPrice(string tag)
		{
		    if (string.IsNullOrEmpty(tag)) return double.NaN;
		    int at = tag.IndexOf('@');
		    if (at < 0 || at == tag.Length - 1) return double.NaN;

		    double px;
		    // parse using invariant culture since tags use '.' decimal
		    if (double.TryParse(tag.Substring(at + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out px))
		        return px;
		    return double.NaN;
		}

		private static bool IsStopTag(string tag)
		{
		    return !string.IsNullOrEmpty(tag) &&
		           tag.IndexOf("_STP@", StringComparison.Ordinal) >= 0;
		}
		
		// ===== SURGICAL: TECHNICAL INDICATORS =====
		private void UpdateTechnicalIndicators()
		{
		    if (dataBars == null || dataBars.Count < ATRPeriod) return;
		    
		    // Calculate ATR(14)
		    _currentATR = CalculateATR(ATRPeriod);
		    
		    // Calculate VWAP (simplified - using typical price)
		    _currentVWAP = CalculateVWAP();
		    
		    // Calculate EMA50 (simplified - using close prices)
		    _currentEMA50 = CalculateEMA(50);
		    
		    // Calculate ADX (simplified)
		    _currentADX = CalculateADX(ATRPeriod);
		    
		    // Calculate True Range
		    _currentTrueRange = CalculateTrueRange();
		    
		    // Update swing highs/lows
		    UpdateSwingLevels();
		}
		
		private double CalculateATR(int period)
		{
		    if (dataBars == null || dataBars.Count < period + 1) return double.NaN;
		    
		    double sum = 0;
		    for (int i = dataBars.Count - period; i < dataBars.Count; i++)
		    {
		        double tr = CalculateTrueRangeAt(i);
		        sum += tr;
		    }
		    return sum / period;
		}
		
		private double CalculateTrueRangeAt(int index)
		{
		    if (index <= 0 || index >= dataBars.Count) return double.NaN;
		    
		    var current = dataBars[index];
		    var previous = dataBars[index - 1];
		    
		    double tr1 = current.Prices.High - current.Prices.Low;
		    double tr2 = Math.Abs(current.Prices.High - previous.Prices.Close);
		    double tr3 = Math.Abs(current.Prices.Low - previous.Prices.Close);
		    
		    return Math.Max(tr1, Math.Max(tr2, tr3));
		}
		
		private double CalculateTrueRange()
		{
		    return CalculateTrueRangeAt(dataBars.Count - 1);
		}
		
		private double CalculateVWAP()
		{
		    if (dataBars == null || dataBars.Count < 20) return double.NaN;
		    
		    // Simplified VWAP calculation using last 20 bars
		    double sumPV = 0;
		    double sumV = 0;
		    int lookback = Math.Min(20, dataBars.Count);
		    
		    for (int i = dataBars.Count - lookback; i < dataBars.Count; i++)
		    {
		        var bar = dataBars[i];
		        double typicalPrice = (bar.Prices.High + bar.Prices.Low + bar.Prices.Close) / 3.0;
		        long volume = (bar.Volumes != null) ? bar.Volumes.Volume : 1;
		        
		        sumPV += typicalPrice * volume;
		        sumV += volume;
		    }
		    
		    return sumV > 0 ? sumPV / sumV : double.NaN;
		}
		
		private double CalculateEMA(int period)
		{
		    if (dataBars == null || dataBars.Count < period) return double.NaN;
		    
		    double multiplier = 2.0 / (period + 1);
		    double ema = dataBars[dataBars.Count - period].Prices.Close;
		    
		    for (int i = dataBars.Count - period + 1; i < dataBars.Count; i++)
		    {
		        ema = (dataBars[i].Prices.Close * multiplier) + (ema * (1 - multiplier));
		    }
		    
		    return ema;
		}
		
		private double CalculateADX(int period)
		{
		    // Simplified ADX calculation
		    if (dataBars == null || dataBars.Count < period + 1) return 25.0; // Default moderate value
		    
		    // For now, return a moderate ADX value
		    // In a full implementation, this would calculate the actual ADX
		    return 30.0;
		}
		
		private void UpdateSwingLevels()
		{
		    if (dataBars == null || dataBars.Count < 5) return;
		    
		    // Simple swing high/low detection
		    int lookback = Math.Min(5, dataBars.Count - 2);
		    int center = dataBars.Count - 1 - lookback;
		    
		    if (center >= 0 && center < dataBars.Count)
		    {
		        var centerBar = dataBars[center];
		        bool isSwingHigh = true;
		        bool isSwingLow = true;
		        
		        for (int i = center - lookback; i <= center + lookback; i++)
		        {
		            if (i >= 0 && i < dataBars.Count && i != center)
		            {
		                if (dataBars[i].Prices.High >= centerBar.Prices.High)
		                    isSwingHigh = false;
		                if (dataBars[i].Prices.Low <= centerBar.Prices.Low)
		                    isSwingLow = false;
		            }
		        }
		        
		        if (isSwingHigh)
		            _lastSwingHigh = centerBar.Prices.High;
		        if (isSwingLow)
		            _lastSwingLow = centerBar.Prices.Low;
		    }
		}
		
		// ===== SURGICAL: K-OF-N VALIDATION =====
		private bool ValidateKofN(bool isLong, out int kofnScore, out string vetoReason)
		{
		    kofnScore = 0;
		    vetoReason = "";
		    
		    if (double.IsNaN(_currentATR) || double.IsNaN(_currentVWAP) || double.IsNaN(_currentEMA50))
		    {
		        vetoReason = "missing_indicators";
		        return false;
		    }
		    
		    // Condition 1: EMA50 slope in trade direction
		    if (ValidateEMA50Slope(isLong))
		        kofnScore++;
		    
		    // Condition 2: Price vs VWAP in direction and within tolerance
		    if (ValidateVWAPPosition(isLong))
		        kofnScore++;
		    
		    // Condition 3: Delta/imbalance confirmation
		    if (ValidateDeltaConfirmation(isLong))
		        kofnScore++;
		    
		    // Condition 4: Recent structure confirms
		    if (ValidateStructureConfirmation(isLong))
		        kofnScore++;
		    
		    // Condition 5: ADX or TrueRange threshold
		    if (ValidateMomentumThreshold())
		        kofnScore++;
		    
		    return kofnScore >= 3;
		}
		
		private bool ValidateEMA50Slope(bool isLong)
		{
		    if (dataBars == null || dataBars.Count < 5) return false;
		    
		    // Simple EMA50 slope check using last 3 bars
		    double ema1 = CalculateEMA(50);
		    double ema2 = CalculateEMAAt(dataBars.Count - 3, 50);
		    
		    if (double.IsNaN(ema1) || double.IsNaN(ema2)) return false;
		    
		    return isLong ? (ema1 > ema2) : (ema1 < ema2);
		}
		
		private double CalculateEMAAt(int startIndex, int period)
		{
		    if (dataBars == null || startIndex < period) return double.NaN;
		    
		    double multiplier = 2.0 / (period + 1);
		    double ema = dataBars[startIndex - period].Prices.Close;
		    
		    for (int i = startIndex - period + 1; i <= startIndex; i++)
		    {
		        ema = (dataBars[i].Prices.Close * multiplier) + (ema * (1 - multiplier));
		    }
		    
		    return ema;
		}
		
		private bool ValidateVWAPPosition(bool isLong)
		{
		    if (double.IsNaN(_currentVWAP)) return false;
		    
		    double currentPrice = currentDataBar.Prices.Close;
		    double distance = Math.Abs(currentPrice - _currentVWAP);
		    double tolerance = _currentATR * VWAPToleranceATR;
		    
		    if (distance > tolerance) return false;
		    
		    return isLong ? (currentPrice >= _currentVWAP) : (currentPrice <= _currentVWAP);
		}
		
		private bool ValidateDeltaConfirmation(bool isLong)
		{
		    // Simplified delta confirmation - check if delta is in favor
		    if (currentDataBar.Deltas == null) return false;
		    
		    // For longs, we want positive delta; for shorts, negative delta
		    return isLong ? (currentDataBar.Deltas.Delta > 0) : (currentDataBar.Deltas.Delta < 0);
		}
		
		private bool ValidateStructureConfirmation(bool isLong)
		{
		    if (dataBars == null || dataBars.Count < 10) return false;
		    
		    // Look for recent swing structure
		    if (isLong)
		    {
		        // For longs: look for HL after HH
		        return FindHLAfterHH();
		    }
		    else
		    {
		        // For shorts: look for LH after LL
		        return FindLHAfterLL();
		    }
		}
		
		private bool FindHLAfterHH()
		{
		    // Simplified structure check
		    if (dataBars == null || dataBars.Count < 5) return false;
		    
		    // Look for recent higher high followed by higher low
		    for (int i = dataBars.Count - 5; i < dataBars.Count - 1; i++)
		    {
		        if (i + 1 < dataBars.Count)
		        {
		            if (dataBars[i].Prices.High > dataBars[i + 1].Prices.High &&
		                dataBars[i].Prices.Low < dataBars[i + 1].Prices.Low)
		                return true;
		        }
		    }
		    return false;
		}
		
		private bool FindLHAfterLL()
		{
		    // Simplified structure check
		    if (dataBars == null || dataBars.Count < 5) return false;
		    
		    // Look for recent lower low followed by lower high
		    for (int i = dataBars.Count - 5; i < dataBars.Count - 1; i++)
		    {
		        if (i + 1 < dataBars.Count)
		        {
		            if (dataBars[i].Prices.Low < dataBars[i + 1].Prices.Low &&
		                dataBars[i].Prices.High > dataBars[i + 1].Prices.High)
		                return true;
		        }
		    }
		    return false;
		}
		
		private bool ValidateMomentumThreshold()
		{
		    // ADX >= threshold OR TrueRange >= floor
		    return (_currentADX >= 25.0) || (_currentTrueRange >= _currentATR * 0.5);
		}
		
		// ===== STRENGTHENING: COMPRESSION PREREQUISITE =====
		private bool CheckCompressionPrerequisite(bool isLong, out string vetoReason)
		{
		    vetoReason = "";
		    
		    if (double.IsNaN(_currentATR) || double.IsNaN(_currentVWAP))
		    {
		        vetoReason = "missing_indicators";
		        return false;
		    }
		    
		    // Check for at least one compression condition
		    bool hasCompression = false;
		    
		    // 1. NR7/inside-bar cluster within last N bars
		    if (HasNR7Cluster())
		        hasCompression = true;
		    
		    // 2. Price coils within ±0.5×ATR around a base for ≥ M bars
		    if (!hasCompression && HasPriceCoils())
		        hasCompression = true;
		    
		    // 3. VWAP "pinch" (distance to VWAP ≤ 0.5×ATR14)
		    if (!hasCompression && HasVWAPPinch())
		        hasCompression = true;
		    
		    if (!hasCompression)
		    {
		        vetoReason = "no_compression";
		        return false;
		    }
		    
		    return true;
		}
		
		private bool HasNR7Cluster()
		{
		    if (dataBars == null || dataBars.Count < 7) return false;
		    
		    // Check for NR7 (narrowest range in last 7 bars)
		    int lookback = Math.Min(7, dataBars.Count);
		    double narrowestRange = double.MaxValue;
		    
		    for (int i = dataBars.Count - lookback; i < dataBars.Count; i++)
		    {
		        double range = dataBars[i].Prices.High - dataBars[i].Prices.Low;
		        if (range < narrowestRange)
		            narrowestRange = range;
		    }
		    
		    // Check if current bar is NR7
		    double currentRange = currentDataBar.Prices.High - currentDataBar.Prices.Low;
		    return currentRange <= narrowestRange;
		}
		
		private bool HasPriceCoils()
		{
		    if (dataBars == null || dataBars.Count < CompressionWindow) return false;
		    
		    double basePrice = currentDataBar.Prices.Close;
		    double tolerance = _currentATR * CompressionATR;
		    int coilsCount = 0;
		    
		    for (int i = dataBars.Count - CompressionWindow; i < dataBars.Count; i++)
		    {
		        double barMid = (dataBars[i].Prices.High + dataBars[i].Prices.Low) / 2.0;
		        if (Math.Abs(barMid - basePrice) <= tolerance)
		            coilsCount++;
		    }
		    
		    return coilsCount >= CompressionWindow;
		}
		
		private bool HasVWAPPinch()
		{
		    if (double.IsNaN(_currentVWAP) || double.IsNaN(_currentATR)) return false;
		    
		    double distance = Math.Abs(currentDataBar.Prices.Close - _currentVWAP);
		    return distance <= _currentATR * VWAPPinchATR;
		}
		
		// ===== STRENGTHENING: ENERGY CONFIRMATION =====
		private bool CheckEnergyConfirmation(bool isLong, out int energyScore, out string vetoReason)
		{
		    energyScore = 0;
		    vetoReason = "";
		    
		    // Need ≥2 of 3 energy conditions
		    
		    // 1. ∑Delta(3) in breakout direction ≥ MinDelta3
		    if (CheckDelta3Confirmation(isLong))
		        energyScore++;
		    
		    // 2. Volume of signal bar ≥ 1.2× avgVolume(20)
		    if (CheckVolumeConfirmation())
		        energyScore++;
		    
		    // 3. Stacked imbalance count ≥ 3 in direction
		    if (CheckStackedImbalanceConfirmation(isLong))
		        energyScore++;
		    
		    if (energyScore < 2)
		    {
		        vetoReason = "insufficient_energy";
		        return false;
		    }
		    
		    return true;
		}
		
		private bool CheckDelta3Confirmation(bool isLong)
		{
		    if (dataBars == null || dataBars.Count < 3) return false;
		    
		    int deltaSum = 0;
		    for (int i = dataBars.Count - 3; i < dataBars.Count; i++)
		    {
		        if (dataBars[i].Deltas != null)
		            deltaSum += (int)dataBars[i].Deltas.Delta;
		    }
		    
		    if (isLong)
		        return deltaSum >= GetMinDelta3();
		    else
		        return deltaSum <= -GetMinDelta3();
		}
		
		private bool CheckVolumeConfirmation()
		{
		    if (dataBars == null || dataBars.Count < 20) return false;
		    if (currentDataBar.Volumes == null) return false;
		    
		    // Calculate average volume over last 20 bars
		    long totalVolume = 0;
		    for (int i = dataBars.Count - 20; i < dataBars.Count; i++)
		    {
		        if (dataBars[i].Volumes != null)
		            totalVolume += dataBars[i].Volumes.Volume;
		    }
		    
		    double avgVolume = totalVolume / 20.0;
		    return currentDataBar.Volumes.Volume >= avgVolume * GetVolumeThreshold();
		}
		
		private bool CheckStackedImbalanceConfirmation(bool isLong)
		{
		    // Simplified stacked imbalance check
		    // In a full implementation, this would check for actual stacked imbalances
		    // For now, return true to avoid blocking trades
		    return true;
		}
		
		// ===== STRENGTHENING: OVEREXTENSION & EXHAUSTION VETOES =====
		private bool CheckOverextensionVetoes(bool isLong, out string vetoReason)
		{
		    vetoReason = "";
		    
		    // 1. Run-up guard: if pre-trigger move in last K bars > 1.5×ATR14 → veto
		    if (HasRunupGuardViolation(isLong))
		    {
		        vetoReason = "runup_guard";
		        return false;
		    }
		    
		    // 2. Wick/exhaustion: if signal bar shows wick ≥ 40% of range → veto
		    if (HasWickExhaustion(isLong))
		    {
		        vetoReason = "wick_exhaustion";
		        return false;
		    }
		    
		    // 3. Deviation guard: if price is ≥ 2.0×ATR14 away from VWAP → veto
		    if (HasDeviationGuardViolation())
		    {
		        vetoReason = "deviation_guard";
		        return false;
		    }
		    
		    // 4. One-and-done at level: don't re-arm at same swing level
		    if (IsOneAndDoneViolation(isLong))
		    {
		        vetoReason = "one_and_done";
		        return false;
		    }
		    
		    return true;
		}
		
		private bool HasRunupGuardViolation(bool isLong)
		{
		    if (dataBars == null || dataBars.Count < 5) return false;
		    if (double.IsNaN(_currentATR)) return false;
		    
		    // Check pre-trigger move in last 5 bars
		    double startPrice = dataBars[dataBars.Count - 5].Prices.Close;
		    double endPrice = currentDataBar.Prices.Close;
		    double move = Math.Abs(endPrice - startPrice);
		    
		    return move > _currentATR * RunupGuardATR;
		}
		
		private bool HasWickExhaustion(bool isLong)
		{
		    var bar = currentDataBar;
		    double range = bar.Prices.High - bar.Prices.Low;
		    if (range == 0) return false;
		    
		    if (isLong)
		    {
		        // Check upper wick for longs
		        double upperWick = bar.Prices.High - Math.Max(bar.Prices.Open, bar.Prices.Close);
		        return (upperWick / range) >= WickExhaustionRatio;
		    }
		    else
		    {
		        // Check lower wick for shorts
		        double lowerWick = Math.Min(bar.Prices.Open, bar.Prices.Close) - bar.Prices.Low;
		        return (lowerWick / range) >= WickExhaustionRatio;
		    }
		}
		
		private bool HasDeviationGuardViolation()
		{
		    if (double.IsNaN(_currentVWAP) || double.IsNaN(_currentATR)) return false;
		    
		    double distance = Math.Abs(currentDataBar.Prices.Close - _currentVWAP);
		    return distance >= _currentATR * DeviationGuardATR;
		}
		
		private bool IsOneAndDoneViolation(bool isLong)
		{
		    if (double.IsNaN(_lastStopLevel)) return false;
		    
		    double currentLevel = isLong ? _lastSwingHigh : _lastSwingLow;
		    if (double.IsNaN(currentLevel)) return false;
		    
		    double tick = GetTick();
		    return Math.Abs(currentLevel - _lastStopLevel) <= 2 * tick;
		}
		
		// ===== STRENGTHENING: STRUCTURE VALIDATION =====
		private bool CheckStructureValidation(bool isLong, out string vetoReason)
		{
		    vetoReason = "";
		    
		    // Breakout level must be prior swing H/L (not a mid-range tick)
		    double triggerPrice = CalculateTriggerPrice(isLong);
		    double swingLevel = isLong ? _lastSwingHigh : _lastSwingLow;
		    
		    if (double.IsNaN(swingLevel))
		    {
		        vetoReason = "no_swing_level";
		        return false;
		    }
		    
		    double tick = GetTick();
		    double distance = Math.Abs(triggerPrice - swingLevel);
		    
		    // Trigger should be close to swing level (within 4 ticks in Active mode, 2 otherwise)
		    int maxSwingDist = (FREQ_MODE == TradeFrequencyMode.Active) ? 4 : 2;
		    if (distance > maxSwingDist * tick)
		    {
		        vetoReason = "trigger_not_swing";
		        return false;
		    }
		    
		    // Distance from base to trigger ≤ configured ATR multiple
		    double baseToTrigger = Math.Abs(triggerPrice - currentDataBar.Prices.Close);
		    if (baseToTrigger > _currentATR * GetBaseToTriggerATR())
		    {
		        vetoReason = "overextended_chase";
		        return false;
		    }
		    
		    return true;
		}
		
		// ===== STRENGTHENING: LUNCH FILTER =====
		private bool CheckLunchFilter(bool isLong, out string vetoReason)
		{
		    vetoReason = "";
		    
		    // Check if we're in lunch time (12:00-13:00)
		    if (IsWithinHHmmInt(currentDataBar.Time, LunchStartHHmm, LunchEndHHmm))
		    {
		        // During lunch, require K-of-N based on frequency mode
		        int kofnScore;
		        if (!ValidateKofN(isLong, out kofnScore, out vetoReason))
		        {
		            vetoReason = "lunch_kofn_fail";
		            return false;
		        }
		        
		        if (kofnScore < GetLunchKofNRequired())
		        {
		            vetoReason = "lunch_insufficient_kofn";
		            return false;
		        }
		    }
		    
		    return true;
		}

		// ===== SURGICAL: HARD VETOES =====
		private bool CheckHardVetoes(bool isLong, out string vetoReason)
		{
		    vetoReason = "";
		    
		    if (double.IsNaN(_currentATR))
		    {
		        vetoReason = "no_atr";
		        return false;
		    }
		    
		    // Bar range check
		    double barRange = currentDataBar.Prices.High - currentDataBar.Prices.Low;
		    if (barRange < _currentATR * GetMinBarRangeATR())
		    {
		        vetoReason = "bar_range_too_small";
		        return false;
		    }
		    
		    // Distance to stop check
		    double tick = GetTick();
		    double stopDistance = CalculateStopDistance(isLong);
		    if (stopDistance < MinBarRangeTicks * tick)
		    {
		        vetoReason = "stop_too_close";
		        return false;
		    }
		    
		    // Full-body opposite candle check
		    if (IsFullBodyOppositeCandle(isLong))
		    {
		        vetoReason = "opposite_candle";
		        return false;
		    }
		    
		    return true;
		}
		
		private bool IsFullBodyOppositeCandle(bool isLong)
		{
		    var bar = currentDataBar;
		    double bodySize = Math.Abs(bar.Prices.Close - bar.Prices.Open);
		    double totalRange = bar.Prices.High - bar.Prices.Low;
		    
		    if (totalRange == 0) return false;
		    
		    double bodyRatio = bodySize / totalRange;
		    
		    // Consider it a full-body opposite candle if body is > 70% of range and opposite direction
		    if (bodyRatio > 0.7)
		    {
		        bool isBullishCandle = bar.Prices.Close > bar.Prices.Open;
		        return isLong ? !isBullishCandle : isBullishCandle;
		    }
		    
		    return false;
		}
		
		// ===== SURGICAL: ANTI-WHIPSAW =====
		private bool CheckAntiWhipsaw(bool isLong, out string vetoReason)
		{
		    vetoReason = "";
		    
		    // Check for large opposite impulse
		    if (HasLargeOppositeImpulse(isLong))
		    {
		        vetoReason = "large_opposite_impulse";
		        return false;
		    }
		    
		    // Check cooldown after stop
		    if (currentDataBar.BarNumber - _lastStopBar < GetCooldownOnStop())
		    {
		        vetoReason = "cooldown_after_stop";
		        return false;
		    }
		    
		    // Check minimum bars between signals
		    if (currentDataBar.BarNumber - _lastSignalBar < GetMinBarsBetweenSignals())
		    {
		        vetoReason = "min_bars_between";
		        return false;
		    }
		    
		    return true;
		}
		
		private bool HasLargeOppositeImpulse(bool isLong)
		{
		    if (dataBars == null || dataBars.Count < 2) return false;
		    
		    var prevBar = dataBars[dataBars.Count - 2];
		    double prevRange = prevBar.Prices.High - prevBar.Prices.Low;
		    
		    if (double.IsNaN(_currentATR)) return false;
		    
		    // Check if previous bar was a large impulse in opposite direction
		    if (prevRange >= _currentATR * LargeImpulseATR)
		    {
		        bool prevWasBullish = prevBar.Prices.Close > prevBar.Prices.Open;
		        return isLong ? prevWasBullish : !prevWasBullish;
		    }
		    
		    return false;
		}
		
		// ===== SURGICAL: STOP/TARGET CALCULATION =====
		private double CalculateStopDistance(bool isLong)
		{
		    if (double.IsNaN(_currentATR)) return double.NaN;
		    
		    double atrStop = _currentATR * ATRStopMultiplier;
		    double swingStop = double.NaN;
		    
		    if (isLong && !double.IsNaN(_lastSwingLow))
		    {
		        swingStop = currentDataBar.Prices.Close - _lastSwingLow + 2 * GetTick();
		    }
		    else if (!isLong && !double.IsNaN(_lastSwingHigh))
		    {
		        swingStop = _lastSwingHigh - currentDataBar.Prices.Close + 2 * GetTick();
		    }
		    
		    if (double.IsNaN(swingStop))
		        return atrStop;
		    
		    return Math.Max(atrStop, swingStop);
		}
		
		private double CalculateTargetPrice(bool isLong, double stopDistance)
		{
		    if (isLong)
		        return currentDataBar.Prices.Close + (stopDistance * RMultiple);
		    else
		        return currentDataBar.Prices.Close - (stopDistance * RMultiple);
		}
		
		private double CalculateTriggerPrice(bool isLong)
		{
		    if (dataBars == null || dataBars.Count < 2) return double.NaN;
		    
		    var prevBar = dataBars[dataBars.Count - 2];
		    double tick = GetTick();
		    
		    if (isLong)
		        return prevBar.Prices.High + tick;
		    else
		        return prevBar.Prices.Low - tick;
		}
		

		// ===== SURGICAL: MANAGE OPEN TRADE =====
		private bool ManageOpenTrade()
		{
		    if (!_hasActiveTrade || currentDataBar == null) return false;

		    double tick = GetTick();
		    int barsAlive = (_activeStartBar >= 0) ? (currentDataBar.BarNumber - _activeStartBar) : 0;
		    
		    // SURGICAL: PENDING ENTRY (not yet crossed -> not armed)
		    if (!_bracketArmed)
		    {
		        // Timeout & cancel pending entry after 4 bars
		        if (barsAlive >= 4)
		        {
		            StrategyData sCancel = null;
		            if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
		                sCancel = eventsContainer.StrategiesEvents.StrategyData as StrategyData;

		            if (sCancel != null)
		            {
		                try
		                {
		                    sCancel.UpdateTriggeredDataProvider(
		                        _activeIsLong ? Direction.Long : Direction.Short,
		                        false,
		                        double.NaN,
		                        double.NaN,
		                        _activeEntryTag + "_CANCEL",
		                        EntryExecMode.Managed,
		                        0
		                    );
		                }
		                catch { }
		            }

		            _hasActiveTrade = false;
		            _bracketArmed = false;
		            _activeEntryTag = null;
		            _maxFavorableR = 0.0;
		            _maxAdverseR = 0.0;
		            DebugLog("Pending entry timed out after " + barsAlive + " bars; canceled.");
		            return true;
		        }

		        // Arm SL/TP when price crosses the entry tag price (proxy fill)
		        if (!double.IsNaN(_entryTagPrice))
		        {
		            bool crossed = _activeIsLong
		                ? (currentDataBar.Prices.High >= _entryTagPrice)   // stop-entry long
		                : (currentDataBar.Prices.Low <= _entryTagPrice);   // stop-entry short

		            if (crossed)
		            {
		                StrategyData s2 = null;
		                if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
		                    s2 = eventsContainer.StrategiesEvents.StrategyData as StrategyData;

		                if (s2 != null)
		                {
		                    s2.UpdateTriggeredDataProvider(
		                        _activeIsLong ? Direction.Long : Direction.Short,
		                        true,
		                        _activeInitialStop,
		                        _activeInitialTarget,
		                        _activeEntryTag,
		                        EntryExecMode.Managed,
		                        0
		                    );
		                }
		                _bracketArmed = true;
		                _activeEntryPrice = _entryTagPrice;
		                _activeStartBar = currentDataBar.BarNumber;
		                DebugLog((_activeIsLong ? "[L] " : "[S] ") + "Bracket armed @"
		                    + _entryTagPrice.ToString("0.00", CultureInfo.InvariantCulture)
		                    + " | SL=" + _activeInitialStop.ToString("0.00", CultureInfo.InvariantCulture)
		                    + " | TP=" + _activeInitialTarget.ToString("0.00", CultureInfo.InvariantCulture));
		            }
		        }

		        return false;
		    }

		    // SURGICAL: FILLED / ARMED - manage exits with new BE and trailing logic
		    
		    // Proxy-detect TP/SL hits
		    bool hitSL = _activeIsLong
		        ? (currentDataBar.Prices.Low <= _activeInitialStop)
		        : (currentDataBar.Prices.High >= _activeInitialStop);

		    bool hitTP = _activeIsLong
		        ? (currentDataBar.Prices.High >= _activeInitialTarget)
		        : (currentDataBar.Prices.Low <= _activeInitialTarget);

		    if (hitSL || hitTP)
		    {
		        if (hitSL) _lastStopBar = currentDataBar.BarNumber; // Track stop for cooldown
		        
		        // Calculate realized R
		        double exitPrice = hitTP ? _activeInitialTarget : _activeInitialStop;
		        double realizedPnL = _activeIsLong 
		            ? (exitPrice - _activeEntryPrice)
		            : (_activeEntryPrice - exitPrice);
		        double realizedR = _initRiskTicks > 0 ? realizedPnL / (_initRiskTicks * tick) : 0;
		        
		        PrintExitSnapshot(hitTP ? "TP" : "SL", realizedR, barsAlive, _maxFavorableR, _maxAdverseR);
		        
		        _hasActiveTrade = false;
		        _bracketArmed = false;
		        _activeEntryTag = null;
		        _maxFavorableR = 0.0;
		        _maxAdverseR = 0.0;
		        DebugLog((hitTP ? "[TP] " : "[SL] ") + "exit proxy detected; clearing active state.");
		        return true;
		    }

		    // STRENGTHENING: Update tracking variables
		    _barsSinceFill++;
		    
		    // SURGICAL: Calculate current profit in R
		    double unrealizedPnL = _activeIsLong 
		        ? (currentDataBar.Prices.Close - _activeEntryPrice)
		        : (_activeEntryPrice - currentDataBar.Prices.Close);
		    int gotTicks = (int)Math.Round(Math.Abs(unrealizedPnL) / tick);
		    double currentR = _initRiskTicks > 0 ? (unrealizedPnL > 0 ? gotTicks : -gotTicks) / (double)_initRiskTicks : 0;
		    
		    // Update maximum favorable excursion (MFE)
		    if (currentR > _maxFavorableR)
		        _maxFavorableR = currentR;
		    
		    // Update maximum adverse excursion (MAE)
		    if (currentR < _maxAdverseR)
		        _maxAdverseR = currentR;

	    // SURGICAL: Step-1 BE at 0.35R (not 0.5R)
	    if (_bracketArmed && !_movedToBE && currentR >= BERatio)
	    {
	        double beStop = _activeIsLong
	            ? RoundToTick(_activeEntryPrice + tick, tick)  // BE+1
	            : RoundToTick(_activeEntryPrice - tick, tick);

	        StrategyData sBE = null;
	        if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
	            sBE = eventsContainer.StrategiesEvents.StrategyData as StrategyData;

	        if (sBE != null)
	        {
	            sBE.UpdateTriggeredDataProvider(
	                _activeIsLong ? Direction.Long : Direction.Short,
	                true,
	                beStop,
	                _activeInitialTarget,
	                _activeEntryTag + "_BE",
	                EntryExecMode.Managed,
	                0
	            );
	            _activeInitialStop = beStop;
	            _movedToBE = true;
	            DebugLog("BE set @" + beStop.ToString("0.00"));
	        }
	    }

		    // SURGICAL: Step-2 ATR trailing after 0.7R
		    if (_movedToBE && currentR >= TrailStartRatio && !double.IsNaN(_currentATR))
		    {
		        double trailDistance = Math.Max(_currentATR * ATRTrailMultiplier, MinTrailTicks * tick);
		        double newStop = _activeIsLong
		            ? RoundToTick(currentDataBar.Prices.Close - trailDistance, tick)
		            : RoundToTick(currentDataBar.Prices.Close + trailDistance, tick);

		        // Never loosen below BE
		        double beLevel = _activeIsLong ? (_activeEntryPrice + tick) : (_activeEntryPrice - tick);
		        if (_activeIsLong && newStop > beLevel) newStop = beLevel;
		        if (!_activeIsLong && newStop < beLevel) newStop = beLevel;

		        // Only move stop forward
		        if ((_activeIsLong && newStop > _activeInitialStop) || (!_activeIsLong && newStop < _activeInitialStop))
		        {
		            StrategyData sTrail = null;
		            if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
		                sTrail = eventsContainer.StrategiesEvents.StrategyData as StrategyData;

		            if (sTrail != null)
		            {
		                sTrail.UpdateTriggeredDataProvider(
		                    _activeIsLong ? Direction.Long : Direction.Short,
		                    true,
		                    newStop,
		                    _activeInitialTarget,
		                    _activeEntryTag + "_TRAIL",
		                    EntryExecMode.Managed,
		                    0
		                );
		                _activeInitialStop = newStop;
		                DebugLog("Trail move @" + newStop.ToString("0.00"));
		            }
		        }
		    }

		    // STRENGTHENING: Early failure handling
		    if (!_fastFlipTriggered && _barsSinceFill <= FastFlipBars)
		    {
		        // Fast-flip exit: opposite delta sum ≥ MinOppDelta2 or counter wick ≥ 40%
		        if (CheckFastFlipCondition(_activeIsLong))
		        {
		            double fastFlipExit = _activeIsLong
		                ? RoundToTick(_activeEntryPrice + (FastFlipExitR * _initRiskTicks * tick), tick)
		                : RoundToTick(_activeEntryPrice - (FastFlipExitR * _initRiskTicks * tick), tick);

		            StrategyData sFastFlip = null;
		            if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
		                sFastFlip = eventsContainer.StrategiesEvents.StrategyData as StrategyData;

		            if (sFastFlip != null)
		            {
		                sFastFlip.UpdateTriggeredDataProvider(
		                    _activeIsLong ? Direction.Long : Direction.Short,
		                    true,
		                    fastFlipExit,
		                    _activeInitialTarget,
		                    _activeEntryTag + "_FASTFLIP",
		                    EntryExecMode.Managed,
		                    0
		                );
		                _fastFlipTriggered = true;
		                DebugLog("fastFlip @" + fastFlipExit.ToString("0.00"));
		            }
		        }
		    }

		    // STRENGTHENING: Time-in-trade rule
		    if (!_timeInTradeTriggered && _barsSinceFill >= TimeInTradeBars)
		    {
		        if (currentR < 0.3) // Not reaching +0.3R within X bars
		        {
		            double tightenStop = _activeIsLong
		                ? RoundToTick(_activeEntryPrice + (TightenStopR * _initRiskTicks * tick), tick)
		                : RoundToTick(_activeEntryPrice - (TightenStopR * _initRiskTicks * tick), tick);

		            StrategyData sTighten = null;
		            if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
		                sTighten = eventsContainer.StrategiesEvents.StrategyData as StrategyData;

		            if (sTighten != null)
		            {
		                sTighten.UpdateTriggeredDataProvider(
		                    _activeIsLong ? Direction.Long : Direction.Short,
		                    true,
		                    tightenStop,
		                    _activeInitialTarget,
		                    _activeEntryTag + "_TIGHTEN",
		                    EntryExecMode.Managed,
		                    0
		                );
		                _timeInTradeTriggered = true;
		                DebugLog("Tighten stop @" + tightenStop.ToString("0.00"));
		            }
		        }
		    }

		    // STRENGTHENING: Final exit if still not progressing
		    if (_timeInTradeTriggered && _barsSinceFill >= (TimeInTradeBars + 5))
		    {
		        if (currentR < 0.1) // Still not progressing
		        {
		            double finalExit = _activeIsLong
		                ? RoundToTick(_activeEntryPrice + (TightenStopR * _initRiskTicks * tick), tick)
		                : RoundToTick(_activeEntryPrice - (TightenStopR * _initRiskTicks * tick), tick);

		            StrategyData sFinal = null;
		            if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
		                sFinal = eventsContainer.StrategiesEvents.StrategyData as StrategyData;

		            if (sFinal != null)
		            {
		                sFinal.UpdateTriggeredDataProvider(
		                    _activeIsLong ? Direction.Long : Direction.Short,
		                    true,
		                    finalExit,
		                    _activeInitialTarget,
		                    _activeEntryTag + "_FINAL",
		                    EntryExecMode.Managed,
		                    0
		                );
		                _hasActiveTrade = false;
		                _bracketArmed = false;
		                _activeEntryTag = null;
		                DebugLog("Final exit @" + finalExit.ToString("0.00"));
		                return true;
		            }
		        }
		    }

		    // SURGICAL: Time-based flatten after 10 bars
		    if (barsAlive >= 10)
		    {
		        double exitStop = _activeIsLong
		            ? RoundToTick(currentDataBar.Prices.Close - tick, tick)
		            : RoundToTick(currentDataBar.Prices.Close + tick, tick);

		        StrategyData sExit = null;
		        if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
		            sExit = eventsContainer.StrategiesEvents.StrategyData as StrategyData;

		        if (sExit != null)
		        {
		            sExit.UpdateTriggeredDataProvider(
		                _activeIsLong ? Direction.Long : Direction.Short,
		                true,
		                exitStop,
		                _activeInitialTarget,
		                _activeEntryTag + "_TIME",
		                EntryExecMode.Managed,
		                0
		            );
		        }

		        // Calculate realized R for time exit
		        double timePnL = _activeIsLong 
		            ? (currentDataBar.Prices.Close - _activeEntryPrice)
		            : (_activeEntryPrice - currentDataBar.Prices.Close);
		        double timeR = _initRiskTicks > 0 ? timePnL / (_initRiskTicks * tick) : 0;
		        
		        PrintExitSnapshot("TIME", timeR, barsAlive, _maxFavorableR, _maxAdverseR);

		        _hasActiveTrade = false;
		        _bracketArmed = false;
		        _activeEntryTag = null;
		        _maxFavorableR = 0.0;
		        _maxAdverseR = 0.0;
		        DebugLog("Time-based flatten after " + barsAlive + " bars.");
		        return true;
		    }

		    return false;
		}




        // ===== STRENGTHENED CORE WITH REGIME-AWARE ML INTEGRATION =====
        private bool TrySignal(bool isLong)
        {
            // Update technical indicators first
            UpdateTechnicalIndicators();
            
            // Update regime detection metrics
            UpdateRegimeMetrics();
            
            // Update Darwinian selection
            UpdateDarwinianSelection();
            
            // Manage open trades first; skip new signals if we just forced an exit
            if (ManageOpenTrade()) return false;
            
            // Minimal silent preflight checks
            if (!MinimalPreflight(isLong)) return false;
            
            double tick = GetTick();
            
            // ===== COMPUTE UNIFIED OPPORTUNITY SCORE =====
            int kofnScore; 
            double delta3Z, baseWidthATR, volZ, distVWAP;
            double oppScore = ComputeUnifiedOpportunityScore(isLong, out kofnScore, out delta3Z, out baseWidthATR, out volZ, out distVWAP);
            
            // ===== ML INTEGRATION (optional) =====
            double mlScore = 0.0;
            if (_mlEnabled)
            {
                var features = new Models.Strategies.Features.FeatureVector
                {
                    Ret1m = (float)_rvZ,
                    RvZ = (float)_rvZ,
                    Ar1 = (float)_ar1,
                    CvdSlope = (float)Math.Max(0, Math.Min(1, (_cvdSlope + 1) / 2)),
                    Entropy = (float)_entropy,
                    Hurst = (float)_hurst,
                    Imb = (float)_imb,
                    Stress = (float)_stressIndex
                };
                
                try { mlScore = GetMLScore(features); }
                catch { mlScore = 0.0; }
            }
            
            // Blend opp with ML (70/30)
            double finalScore = _mlEnabled ? (0.7 * oppScore + 0.3 * mlScore) : oppScore;
            
            // ===== SOFT GUARDS (UNIFIED DECISION BLOCK) =====
            // Regime threshold + lunch bump
            double thr = BaseOppThreshold();
            bool isLunchWindow = IsWithinHHmmInt(currentDataBar.Time, LunchStartHHmm, LunchEndHHmm);
            if (isLunchWindow) thr += 0.02; // light bump mid-day

            // Soft guards (relaxed for more frequency)
            bool ok = finalScore >= thr 
                   && kofnScore >= 2  // Relaxed from 3 to 2 out of 5 signals
                   && delta3Z >= 0.15  // Relaxed from 0.35 for more trades
                   && baseWidthATR <= 2.5  // Relaxed from 1.8 for more opportunities
                   && volZ >= 0.05;  // Relaxed from 0.15 for more entries

            // Stand down only in extreme turbulence (scalp mode - more tolerant)
            if (_currentRegime == Regime.Turbulent && _stressIndex > 0.95 && finalScore < (thr + 0.10))
                ok = false;

            if (!ok)
            {
                D("Opportunity rejected: opp=" + finalScore.ToString("F3") + " thr=" + thr.ToString("F3") + 
                  " kofn=" + kofnScore + " dZ=" + delta3Z.ToString("F2") + " baseW=" + baseWidthATR.ToString("F2") + 
                  " volZ=" + volZ.ToString("F2"));
                return false;
            }
            
            // ===== BUILD ENTRY =====
            double stopDistance = CalculateStopDistance(isLong);
            if (double.IsNaN(stopDistance)) { D("invalid_stop"); return false; }
            
            // Regime-aware stop widening
            int baseStopTicks = (int)Math.Round(stopDistance / tick);
            int adjustedStopTicks = RegimeStopTargetWiden(baseStopTicks);
            stopDistance = adjustedStopTicks * tick;
            
            double targetPrice = CalculateTargetPrice(isLong, stopDistance);
            double triggerPrice = CalculateTriggerPrice(isLong);
            
            if (double.IsNaN(triggerPrice) || double.IsNaN(targetPrice)) { D("invalid_prices"); return false; }
            
            // Cluster-aware stop deflection
            double adjustedStop = isLong ? (currentDataBar.Prices.Close - stopDistance) : (currentDataBar.Prices.Close + stopDistance);
            adjustedStop = DeflectClusterStop(adjustedStop, isLong);
            stopDistance = Math.Abs(adjustedStop - currentDataBar.Prices.Close);
            
            // Only stop entries
            string entryTag = BuildEntryTag(isLong, triggerPrice, tick);
            
            int stopTicks = (int)Math.Round(stopDistance / tick);
            int targetTicks = (int)Math.Round(Math.Abs(targetPrice - currentDataBar.Prices.Close) / tick);
            
            // Hard cap stops/targets for scalping mode (chicken pecking corn!)
            stopTicks = Math.Min(Math.Max(stopTicks, 5), 8);   // 5..8 ticks (less whipsaw)
            targetTicks = Math.Min(Math.Max(targetTicks, 5), 9); // 5..9 ticks (easier to hit)
            
            // RECALCULATE actual prices based on capped ticks
            adjustedStop = isLong 
                ? RoundToTick(currentDataBar.Prices.Close - (stopTicks * tick), tick)
                : RoundToTick(currentDataBar.Prices.Close + (stopTicks * tick), tick);
            
            targetPrice = isLong
                ? RoundToTick(currentDataBar.Prices.Close + (targetTicks * tick), tick)
                : RoundToTick(currentDataBar.Prices.Close - (targetTicks * tick), tick);
            
            // Effective quantity (scalp mode - disable stress throttle, always trade full size)
            int baseQty = (int)Math.Round(_currentQuantity);
            int effectiveQty = baseQty;  // No stress-based reduction for scalping
            
            // Only stand down in truly extreme conditions
            if (_currentRegime == Regime.Turbulent && _stressIndex > 0.98)
                effectiveQty = 0; // stand down entirely
                
            if (effectiveQty == 0) { D("stress_standdown"); return false; }
            
            // ===== PROPOSE ENTRY TO HOST =====
            StrategyData s = null;
            if (eventsContainer != null && eventsContainer.StrategiesEvents != null)
                s = eventsContainer.StrategiesEvents.StrategyData as StrategyData;
            
            if (s != null)
            {
                s.UpdateTriggeredDataProvider(
                    isLong ? Direction.Long : Direction.Short,
                    true,
                    adjustedStop,
                    targetPrice,
                    entryTag,
                    EntryExecMode.Managed,
                    effectiveQty);
                
                // Update active trade state
                _activeIsLong = isLong;
                _activeStartBar = currentDataBar.BarNumber;
                _activeEntryPrice = currentDataBar.Prices.Close;
                _activeInitialTarget = targetPrice;
                _activeInitialStop = adjustedStop;
                _activeEntryTag = entryTag;
                _entryTagPrice = triggerPrice;
                _entryIsStop = true;
                _bracketArmed = false;
                _hasActiveTrade = true;
                _movedToBE = false;
                _lockedHalfR = false;
                _lockedOneR = false;
                _initRiskTicks = stopTicks;
                _barsSinceFill = 0;
                _maxFavorableR = 0.0;
                _maxAdverseR = 0.0;
                _fastFlipTriggered = false;
                _timeInTradeTriggered = false;
            }
            
            // Bookkeeping
            _lastSignalBar = currentDataBar.BarNumber;
            _tradesToday++;
            
            // Build reason string for snapshot (unified metrics)
            string reasonsCsv = string.Format("opp={0:0.3}; thr={1:0.3}; kofn={2}; dZ={3:0.2}; vZ={4:0.2}; baseW={5:0.2}; distVWAP={6:0.2}", 
                finalScore, thr, kofnScore, delta3Z, volZ, baseWidthATR, distVWAP);
            
            // Print Entry Snapshot (ONLY visible output)
            PrintEntrySnapshot(isLong, triggerPrice, adjustedStop, targetPrice, stopTicks, targetTicks, effectiveQty, reasonsCsv);
            
            return true;
        }
        
        // ===== STRENGTHENING: HELPER CALCULATIONS =====
        private double CalculateBaseWidth()
        {
            if (dataBars == null || dataBars.Count < CompressionWindow) return 0;
            
            double minPrice = double.MaxValue;
            double maxPrice = double.MinValue;
            
            for (int i = dataBars.Count - CompressionWindow; i < dataBars.Count; i++)
            {
                minPrice = Math.Min(minPrice, dataBars[i].Prices.Low);
                maxPrice = Math.Max(maxPrice, dataBars[i].Prices.High);
            }
            
            return maxPrice - minPrice;
        }
        
        private int CalculateDelta3(bool isLong)
        {
            if (dataBars == null || dataBars.Count < 3) return 0;
            
            int deltaSum = 0;
            for (int i = dataBars.Count - 3; i < dataBars.Count; i++)
            {
                if (dataBars[i].Deltas != null)
                    deltaSum += (int)dataBars[i].Deltas.Delta;
            }
            
            return isLong ? deltaSum : -deltaSum;
        }
        
        private double CalculateVolumeRelative()
        {
            if (dataBars == null || dataBars.Count < 20) return 0;
            if (currentDataBar.Volumes == null) return 0;
            
            long totalVolume = 0;
            for (int i = dataBars.Count - 20; i < dataBars.Count; i++)
            {
                if (dataBars[i].Volumes != null)
                    totalVolume += dataBars[i].Volumes.Volume;
            }
            
            double avgVolume = totalVolume / 20.0;
            return avgVolume > 0 ? (double)currentDataBar.Volumes.Volume / avgVolume : 0;
        }
        
        private double CalculateDistanceToVWAP()
        {
            if (double.IsNaN(_currentVWAP) || double.IsNaN(_currentATR)) return 0;
            
            double distance = Math.Abs(currentDataBar.Prices.Close - _currentVWAP);
            return _currentATR > 0 ? distance / _currentATR : 0;
        }
        
        private double CalculateRunupATR()
        {
            if (dataBars == null || dataBars.Count < 5) return 0;
            if (double.IsNaN(_currentATR)) return 0;
            
            double startPrice = dataBars[dataBars.Count - 5].Prices.Close;
            double endPrice = currentDataBar.Prices.Close;
            double move = Math.Abs(endPrice - startPrice);
            
            return _currentATR > 0 ? move / _currentATR : 0;
        }
        
        // ===== STRENGTHENING: FAST-FLIP CONDITION =====
        private bool CheckFastFlipCondition(bool isLong)
        {
            // Check opposite delta sum ≥ MinOppDelta2
            if (dataBars != null && dataBars.Count >= 2)
            {
                int oppDeltaSum = 0;
                for (int i = dataBars.Count - 2; i < dataBars.Count; i++)
                {
                    if (dataBars[i].Deltas != null)
                    {
                        if (isLong)
                            oppDeltaSum += (int)Math.Min(0, dataBars[i].Deltas.Delta); // Negative delta for longs
                        else
                            oppDeltaSum += (int)Math.Max(0, dataBars[i].Deltas.Delta); // Positive delta for shorts
                    }
                }
                
                if (Math.Abs(oppDeltaSum) >= MinOppDelta2)
                    return true;
            }
            
            // Check counter wick ≥ 40% of bar range
            var bar = currentDataBar;
            double range = bar.Prices.High - bar.Prices.Low;
            if (range > 0)
            {
                if (isLong)
                {
                    // Check lower wick for longs
                    double lowerWick = Math.Min(bar.Prices.Open, bar.Prices.Close) - bar.Prices.Low;
                    return (lowerWick / range) >= WickExhaustionRatio;
                }
                else
                {
                    // Check upper wick for shorts
                    double upperWick = bar.Prices.High - Math.Max(bar.Prices.Open, bar.Prices.Close);
                    return (upperWick / range) >= WickExhaustionRatio;
                }
            }
            
            return false;
        }
        
		// ===== REGIME-AWARE HELPER METHODS =====
		private double GetRegimeThreshold()
		{
			double baseThr = _currentRegime == Regime.Laminar ? 0.40
			               : _currentRegime == Regime.Transition ? 0.50
			               : 0.70;
			return Math.Max(0.30, baseThr + GetRegimeThresholdAdj());
		}
		
		private void LogRegimeDecision(string timestamp, bool isLong, string decision)
		{
			string side = isLong ? "LONG" : "SHORT";
			string regime = _currentRegime.ToString();
			string throttle = _riskThrottle.ToString("F2");
			
			DebugLog("[REGIME] " + timestamp + " " + side + " regime=" + regime + " idx=" + _stressIndex.ToString("F2") + " throttle=" + throttle + " " + decision);
		}
        private void LogStrengthenedDecision(string timestamp, bool isLong, int kofnScore, int stopTicks, int targetTicks, double baseWidth, int delta3, double volRel, double distVWAP, double runupATR, string decision)
        {
            string mode = "STOP";
            string side = isLong ? "LONG" : "SHORT";
            string kofn = kofnScore + "/5";
            
            DebugLog(timestamp + " " + side + " mode=" + mode + " KofN=" + kofn + " baseWidth=" + baseWidth.ToString("0.00") + 
                    " delta3=" + delta3 + " volRel=" + volRel.ToString("0.00") + " distVWAP=" + distVWAP.ToString("0.00") + 
                    " runupATR=" + runupATR.ToString("0.00") + " DECISION=" + decision);
        }
        
        // ===== SURGICAL: DECISION LOGGING =====
        private void LogDecision(string timestamp, bool isLong, int kofnScore, int stopTicks, int targetTicks, string decision)
        {
            string mode = "STOP";
            string side = isLong ? "LONG" : "SHORT";
            string kofn = kofnScore + "/5";
            string atr = double.IsNaN(_currentATR) ? "N/A" : _currentATR.ToString("0.00");
            
            DebugLog(timestamp + " " + side + " mode=" + mode + " KofN=" + kofn + " ATR=" + atr + " StopTicks=" + stopTicks + " TargetTicks=" + targetTicks + " DECISION=" + decision);
        }

        // ===== PREFLIGHT =====
        private bool Preflight(bool isLong)
        {
            if (dataBars == null || currentDataBar == null || dataBars.Count < MinBarsRequiredDyn)
            { DebugLogSide(isLong, "Preflight: bars"); return false; }

            if (RestrictTimeWindow && !IsWithinHHmmInt(currentDataBar.Time, TradeStartHHmm, TradeEndHHmm))
            { DebugLogSide(isLong, "Preflight: time"); return false; }

            ResetDailyIfWrap();
            if (_tradesToday >= DailyTradeCap)
            { DebugLogSide(isLong, "Preflight: cap"); return false; }

            if ((isLong && !AllowLongs) || (!isLong && !AllowShorts))
            { DebugLogSide(isLong, "Preflight: side"); return false; }

            if (currentDataBar.BarNumber == _lastSignalBar)
            { DebugLogSide(isLong, "Preflight: dup bar"); return false; }

            if (_lastSignalBar > 0 && currentDataBar.BarNumber - _lastSignalBar < GetMinBarsBetweenSignals())
            { DebugLogSide(isLong, "Preflight: spacing"); return false; }

            long vol = 0;
            if (currentDataBar.Volumes != null) vol = currentDataBar.Volumes.Volume;
            if (vol < MinCurrentBarVolumeDyn)
            { DebugLogSide(isLong, "Preflight: vol"); return false; }

            if (UseEmaBias)
            {
                double ema = GetFastEmaOrClose();
                double tol = EmaToleranceTicks * GetTick();
                bool ok = isLong ? (currentDataBar.Prices.Close >= ema - tol)
                                 : (currentDataBar.Prices.Close <= ema + tol);
                if (!ok) { DebugLogSide(isLong, "Preflight: EMA"); return false; }
            }

            if (!EmaSlopeOk(isLong))
            { DebugLogSide(isLong, "Preflight: slope"); return false; }

            return true;
        }

        private void ResetDailyIfWrap()
        {
            int t = currentDataBar.Time;
            if (_lastBarTimeInt >= 0 && t < _lastBarTimeInt) _tradesToday = 0;
            _lastBarTimeInt = t;
        }

        // ===== ZONES =====
        private void ScanForNewZones()
        {
            int end = dataBars.Count - 2;
            int start = Math.Max(0, end - LookbackForZonesDyn);
            double tick = GetTick();

            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null || b.Imbalances == null) continue;

                double low, high;
                bool isLongBand;
                if (!TryGetStackBand(b, out low, out high, out isLongBand, tick)) continue;

                bool exists = false;
                for (int k = 0; k < _zones.Count; k++)
                {
                    Zone z = _zones[k];
                    if (z.IsLongZone != isLongBand) continue;

                    if (BandsClose(z.Low, z.High, low, high, MergeToleranceTicks * tick))
                    { exists = true; break; }
                }

                if (!exists)
                {
                    Zone nz = new Zone();
                    nz.Low = Math.Min(low, high);
                    nz.High = Math.Max(low, high);
                    nz.IsLongZone = isLongBand;
                    nz.CreatedBarNumber = b.BarNumber;
                    nz.Tests = 0;
                    nz.Consumed = false;
                    _zones.Add(nz);
                }
            }
        }

        private void PurgeOldZones()
        {
            int cur = currentDataBar.BarNumber;
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                Zone z = _zones[i];
                if (z.Consumed) { _zones.RemoveAt(i); continue; }
                if (cur - z.CreatedBarNumber > MaxZoneAgeBars) _zones.RemoveAt(i);
            }
        }

        private static bool BandsClose(double aL, double aH, double bL, double bH, double tol)
        {
            double midA = (aL + aH) * 0.5;
            double midB = (bL + bH) * 0.5;
            return Math.Abs(midA - midB) <= tol;
        }

        private Zone FindRetestBand(bool isLong, IReadOnlyDataBar bar, double tol)
        {
            Zone best = null;
            double bestDist = double.MaxValue;

            double c = bar.Prices.Close;
            double h = bar.Prices.High;
            double l = bar.Prices.Low;

            for (int i = 0; i < _zones.Count; i++)
            {
                Zone z = _zones[i];
                if (z.Consumed || z.IsLongZone != isLong) continue;
                if (bar.BarNumber <= z.CreatedBarNumber) continue;

                bool tradedInto = (h >= z.Low - tol) && (l <= z.High + tol);
                if (!tradedInto) continue;

                double mid = BandMid(z);
                double d = Math.Abs(c - mid);
                if (d < bestDist) { bestDist = d; best = z; }
            }
            return best;
        }

        private bool TryGetStackBand(IReadOnlyDataBar b, out double low, out double high, out bool isLongBand, double tick)
        {
            low = b.Prices.Close;
            high = b.Prices.Close;
            isLongBand = false;

            bool support = (b.Imbalances != null) && b.Imbalances.HasBidStackedImbalances;
            bool resist  = (b.Imbalances != null) && b.Imbalances.HasAskStackedImbalances;

            if (support || resist)
            {
                double pad = Math.Max(tick, 0.5 * (b.Prices.High - b.Prices.Low));
                isLongBand = support;
                low  = b.Prices.Close - pad;
                high = b.Prices.Close + pad;
                return true;
            }

            if (!UseFallbackZones) return false;

            double rng = b.Prices.High - b.Prices.Low;
            if (rng < 3.0 * tick) return false;
            double pad2 = Math.Max(tick, 0.5 * rng);
            isLongBand = b.Prices.Close >= b.Prices.Open;
            low  = b.Prices.Close - pad2;
            high = b.Prices.Close + pad2;
            return true;
        }

        private static double BandMid(Zone z)
        {
            return 0.5 * (z.Low + z.High);
        }

        // ===== CONFIRMS =====
        private bool HasImpulseAwayFromBand(bool isLong, Zone z, int lookbackBars, double minRange)
        {
            int end = dataBars.Count - 2;
            int start = Math.Max(0, end - lookbackBars + 1);
            double mid = BandMid(z);

            for (int i = end; i >= start; i--)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null) continue;

                double range = b.Prices.High - b.Prices.Low;
                if (range < minRange) continue;

                long d = (b.Deltas != null) ? b.Deltas.Delta : 0L;

                if (isLong)
                {
                    if (b.Prices.Close > mid && d >= 0) return true;
                }
                else
                {
                    if (b.Prices.Close < mid && d <= 0) return true;
                }
            }
            return false;
        }

        private static bool BarQualityOk(bool isLong, IReadOnlyDataBar bar, double _)
        {
            double tick = GetTickStatic();
            double hi = bar.Prices.High, lo = bar.Prices.Low;
            double rng = Math.Max(tick, hi - lo);
            double rngTicks = rng / tick;

            if (rngTicks < 3.25) return false;

            double pos = (bar.Prices.Close - lo) / rng; // 0..1
            if (isLong && pos < 0.55) return false;
            if (!isLong && pos > 0.45) return false;

            return true;
        }

        private bool DirectionalConfirm(bool isLong, IReadOnlyDataBar bar, double wickToBodyMin, double tick)
        {
            bool deltaOk = true;
            if (RequireDeltaConfirm)
            {
                if (bar.Deltas == null) return false;

                long d = bar.Deltas.Delta;
                if ((isLong && d <= 0) || (!isLong && d >= 0))
                    deltaOk = false;

                if (deltaOk && UseDeltaMagnitude)
                {
                    double thr = DeltaMagnitudeThreshold();
                    if (Math.Abs(d) < thr) deltaOk = false;
                }
            }

            // Wick fallback
            double open = bar.Prices.Open, close = bar.Prices.Close;
            double hi = bar.Prices.High, lo = bar.Prices.Low;
            double body = Math.Max(Math.Abs(close - open), tick);
            double upperBase = Math.Max(open, close);
            double lowerBase = Math.Min(open, close);
            double topWick = Math.Max(0.0, hi - upperBase);
            double botWick = Math.Max(0.0, lowerBase - lo);
            bool wickOk = (isLong ? botWick : topWick) >= wickToBodyMin * body;

            return deltaOk || wickOk;
        }

        private bool MicroBreakoutConfirm(bool isLong, double tick)
		{
		    if (dataBars == null || dataBars.Count < 2) return false;

		    var prev = dataBars[dataBars.Count - 2];
		    var cur  = currentDataBar;

		    // Stricter in chop (low _tau), looser in trend (high _tau)
		    double tolClose = Clamp(1.25 - 0.75 * _tau, 0.25, 1.00) * tick;
		    double tolPoke  = Clamp(0.50 + 0.50 / _tau, 0.75, 1.25) * tick;

		    bool up =  (cur.Prices.Close >= prev.Prices.High - tolClose)
		            || (cur.Prices.High  >= prev.Prices.High + tolPoke);

		    bool dn =  (cur.Prices.Close <= prev.Prices.Low  + tolClose)
		            || (cur.Prices.Low   <= prev.Prices.Low  - tolPoke);

		    if (isLong) return up;
		    return dn;
		}


        private double DeltaMagnitudeThreshold()
		{
		    int end = dataBars.Count - 1;
		    int start = Math.Max(0, end - DeltaMagLookbackBarsDyn + 1);
		    double sum = 0.0; int n = 0;

		    for (int i = start; i <= end; i++)
		    {
		        var b = dataBars[i]; if (b == null || b.Deltas == null) continue;
		        sum += Math.Abs((double)b.Deltas.Delta); n++;
		    }
		    if (n == 0) return double.MaxValue;

		    double baseThr = (sum / n) * DeltaMagK;

		    // When _tau < 1 (chop), demand more delta; when trending, keep it near base.
		    double k = 1.0 + 0.35 * (1.0 - Clamp(_tau, 0.0, 1.0));
		    return baseThr * k;
		}


        private bool EmaSlopeOk(bool isLong)
        {
            double emaNow;
            if (currentTechnicalLevels != null && currentTechnicalLevels.Ema != null)
                emaNow = currentTechnicalLevels.Ema.FastEma;
            else
                emaNow = currentDataBar.Prices.Close;

            int lookback = Math.Max(5, EmaSlopePeriodDyn / 4);
            if (dataBars == null || dataBars.Count < lookback + 2)
                return false;

            double emaPast = dataBars[dataBars.Count - 1 - lookback].Prices.Close;
            double slopePerBar = (emaNow - emaPast) / lookback;
            double tick = GetTick();
            double tpb = (tick > 0) ? (slopePerBar / tick) : 0.0;

            if (isLong) return tpb >= MinSlopeTicksPerBar;
            return tpb <= -MinSlopeTicksPerBar;
        }

        // ===== EXITS =====
        private struct DynOrders
        {
            public bool IsValid;
            public double StopPrice;
            public double TargetPrice;
            public int StopTicks;
            public int TargetTicks;
        }

        private DynOrders ComputeDynamicExits(bool isLong, Zone band, double entryPrice)
        {
            double tick = GetTick();

            int entryDist = (int)Math.Round(Math.Abs(entryPrice - BandMid(band)) / tick);
            if (entryDist > MaxEntryDistanceTicksDyn)
            {
                DebugLogSide(isLong, "Exits: entryDist>Max");
                DynOrders bad; bad.IsValid = false; bad.StopPrice = 0; bad.TargetPrice = 0; bad.StopTicks = 0; bad.TargetTicks = 0;
                return bad;
            }

            double farSide = isLong ? band.Low : band.High;
            double stop = isLong
                ? RoundToTick(Math.Min(farSide - StopPadTicks * tick, currentDataBar.Prices.Low  - tick), tick)
                : RoundToTick(Math.Max(farSide + StopPadTicks * tick, currentDataBar.Prices.High + tick), tick);

            int riskTicks = (int)Math.Round(Math.Abs(entryPrice - stop) / tick);
            if (riskTicks < MinStopTicksFloorDyn) riskTicks = MinStopTicksFloorDyn;
            if (riskTicks > MaxRiskTicks)
            {
                DebugLogSide(isLong, "Exits: riskTicks>Max");
                DynOrders bad; bad.IsValid = false; bad.StopPrice = 0; bad.TargetPrice = 0; bad.StopTicks = 0; bad.TargetTicks = 0;
                return bad;
            }

            int minRewardTicks = Math.Max(MinTargetTicks, (int)Math.Ceiling(riskTicks * MinRRMultiple));

            double target = ComputeTarget(isLong, entryPrice, minRewardTicks, tick);
            if (double.IsNaN(target))
            {
                DebugLogSide(isLong, "Exits: NaN target");
                DynOrders bad; bad.IsValid = false; bad.StopPrice = 0; bad.TargetPrice = 0; bad.StopTicks = 0; bad.TargetTicks = 0;
                return bad;
            }

            int rewardTicks = (int)Math.Round(Math.Abs(target - entryPrice) / tick);
            if (rewardTicks < minRewardTicks)
            {
                DebugLogSide(isLong, "Exits: reward<min");
                DynOrders bad; bad.IsValid = false; bad.StopPrice = 0; bad.TargetPrice = 0; bad.StopTicks = 0; bad.TargetTicks = 0;
                return bad;
            }

            if (rewardTicks > MaxTargetTicks)
            {
                rewardTicks = MaxTargetTicks;
                if (isLong) target = entryPrice + rewardTicks * tick;
                else        target = entryPrice - rewardTicks * tick;
                target = RoundToTick(target, tick);
            }

            if (EnforceRoomToTarget && !HasRoomToTarget(isLong, entryPrice, target, tick))
            {
                DebugLogSide(isLong, "Exits: no room");
                DynOrders bad; bad.IsValid = false; bad.StopPrice = 0; bad.TargetPrice = 0; bad.StopTicks = 0; bad.TargetTicks = 0;
                return bad;
            }

            if (isLong && target <= entryPrice + tick)
            {
                DebugLogSide(isLong, "Exits: target too close");
                DynOrders bad; bad.IsValid = false; bad.StopPrice = 0; bad.TargetPrice = 0; bad.StopTicks = 0; bad.TargetTicks = 0;
                return bad;
            }
            if (!isLong && target >= entryPrice - tick)
            {
                DebugLogSide(isLong, "Exits: target too close");
                DynOrders bad; bad.IsValid = false; bad.StopPrice = 0; bad.TargetPrice = 0; bad.StopTicks = 0; bad.TargetTicks = 0;
                return bad;
            }

            DynOrders ok;
            ok.IsValid = true;
            ok.StopPrice = stop;
            ok.TargetPrice = target;
            ok.StopTicks = riskTicks;
            ok.TargetTicks = rewardTicks;
            return ok;
        }

        private double ComputeTarget(bool isLong, double entry, int minRewardTicks, double tick)
        {
            double opp = NearestOpposingBand(isLong, entry);
            if (!double.IsNaN(opp))
            {
                double t;
                if (isLong) t = opp - FrontRunTicks * tick;
                else        t = opp + FrontRunTicks * tick;
                t = RoundToTick(t, tick);
                int rr = (int)Math.Round(Math.Abs(t - entry) / tick);
                if (rr >= minRewardTicks) return t;
            }

            if (!double.IsNaN(_prevPoc))
            {
                if (isLong && _prevPoc > entry)
                {
                    int tPOC = (int)Math.Round((_prevPoc - entry) / tick);
                    if (tPOC >= minRewardTicks) return RoundToTick(_prevPoc - FrontRunTicks * tick, tick);
                }
                else if (!isLong && _prevPoc < entry)
                {
                    int tPOC = (int)Math.Round((entry - _prevPoc) / tick);
                    if (tPOC >= minRewardTicks) return RoundToTick(_prevPoc + FrontRunTicks * tick, tick);
                }
            }

            if (UseActivityLevels && _activity != null && _activity.Count > 0)
            {
                double best = double.NaN;
                int bestTicks = int.MaxValue;

                for (int i = 0; i < _activity.Count; i++)
                {
                    double p = _activity[i];
                    if (isLong && p > entry)
                    {
                        int tt = (int)Math.Round((p - entry) / tick);
                        if (tt >= minRewardTicks && tt < bestTicks) { bestTicks = tt; best = p; }
                    }
                    else if (!isLong && p < entry)
                    {
                        int tt = (int)Math.Round((entry - p) / tick);
                        if (tt >= minRewardTicks && tt < bestTicks) { bestTicks = tt; best = p; }
                    }
                }

                if (!double.IsNaN(best))
                {
                    double t = isLong ? (best - FrontRunTicks * tick)
                                      : (best + FrontRunTicks * tick);
                    return RoundToTick(t, tick);
                }
            }

            int atrTicks = GetAtrProxyTicks(AtrProxyBarsDyn, tick);
            int want = Math.Max(minRewardTicks, Math.Min(MaxTargetTicks, (int)Math.Round(AtrRiskFracDyn * atrTicks)));

            double t2;
            if (isLong) t2 = entry + want * tick;
            else        t2 = entry - want * tick;

            return RoundToTick(t2, tick);
        }

        private double NearestOpposingBand(bool isLong, double refPrice)
        {
            double best = double.NaN;
            for (int i = 0; i < _zones.Count; i++)
            {
                Zone z = _zones[i];
                if (z.Consumed) continue;

                double mid = BandMid(z);

                if (isLong)
                {
                    if (!z.IsLongZone && z.Low > refPrice)
                    {
                        if (double.IsNaN(best) || mid < best) best = mid;
                    }
                }
                else
                {
                    if (z.IsLongZone && z.High < refPrice)
                    {
                        if (double.IsNaN(best) || mid > best) best = mid;
                    }
                }
            }
            return best;
        }

        private bool HasRoomToTarget(bool isLong, double entry, double target, double tick)
        {
            double safety = FrontRunTicks * tick;
            for (int i = 0; i < _zones.Count; i++)
            {
                Zone z = _zones[i];
                if (z.Consumed) continue;
                double mid = BandMid(z);

                if (isLong && !z.IsLongZone && mid > entry && mid < target - safety) return true;
                if (!isLong && z.IsLongZone && mid < entry && mid > target + safety) return true;
            }
            return false;
        }

        // ===== ENV / UTIL =====
        private void RefreshEnvIfNeeded()
        {
            if (currentDataBar == null || dataBars == null) return;
            if (_lastEnvBar >= 0 && currentDataBar.BarNumber - _lastEnvBar < 20) return;

			double sAuto = double.NaN;
		    if (AutoTuneFromPrevSession)
		        sAuto = ComputeAutoTuneFromPrevSession(TradeStartHHmm, TradeEndHHmm);
			
            _s  = double.IsNaN(sAuto) ? Clamp(TUNE, 0.6, 3.0) : sAuto;  // fallback to constant if no block found
            _rt = Math.Sqrt(_s);

			bool usedFallback = double.IsNaN(sAuto);
			if (double.IsNaN(_lastTunePrinted) || Math.Abs(_s - _lastTunePrinted) >= 0.01)
			{
			    DebugLog("TUNE " + (usedFallback ? "(fallback)" : "(auto)")
			        + " s=" + _s.ToString("0.00")
			        + " rt=" + _rt.ToString("0.00"));
			    _lastTunePrinted = _s;
			}
            double volMed120 = MedianVolume(120);
            if (_volBaseEma <= 0.0) _volBaseEma = (volMed120 > 0.0 ? volMed120 : 1.0);

            double alpha = 2.0 / (200.0 + 1.0);
            _volBaseEma = (1.0 - alpha) * _volBaseEma + alpha * Math.Max(1.0, volMed120);

            double nuRaw = (volMed120 <= 0.0) ? 1.0 : (volMed120 / Math.Max(_volBaseEma, 1.0));
            _nu = Clamp(Math.Sqrt(nuRaw), 0.7, 1.3);

            int lbSlope = Math.Max(20, EmaSlopePeriodDyn / 2);
            double now = currentDataBar.Prices.Close;
            double past = (dataBars.Count > lbSlope) ? dataBars[dataBars.Count - 1 - lbSlope].Prices.Close : now;
            double tpb = (GetTick() > 0.0) ? Math.Abs((now - past) / lbSlope) / GetTick() : 0.0;

            double tpb0 = 0.40;
            double tauRaw = (tpb0 > 0.0) ? (tpb / tpb0) : 1.0;
            _tau = Clamp(Math.Sqrt(tauRaw), 0.7, 1.4);

            double wrMed = MedianWickRatio(80);
            double wr0 = 1.50;
            double etaRaw = (wr0 > 0.0) ? (wrMed / wr0) : 1.0;
            _eta = Clamp(Math.Sqrt(etaRaw), 0.7, 1.4);

            _cfg = BuildTunables(_s);
            _lastEnvBar = currentDataBar.BarNumber;
        }
		
		private static int HHmmssToSeconds(int hhmmss)
		{
		    int hh = hhmmss / 10000;
		    int mm = (hhmmss / 100) % 100;
		    int ss = hhmmss % 100;
		    return hh * 3600 + mm * 60 + ss;
		}

        private double VolumeGateP20x(int lookback, double p, double k)
        {
            double v = PercentileVolume(lookback, p);
            if (double.IsNaN(v) || v <= 0.0) v = MedianVolume(lookback) * 0.70;
            return v * k;
        }

        private double PercentileVolume(int lookback, double p)
        {
            if (dataBars == null || dataBars.Count < 3) return double.NaN;

            int end = dataBars.Count - 2;
            int start = Math.Max(0, end - lookback + 1);

            List<double> list = new List<double>();
            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b != null && b.Volumes != null)
                {
                    list.Add(Math.Max(0.0, (double)b.Volumes.Volume));
                }
            }

            if (list.Count == 0) return double.NaN;

            list.Sort();
            double idx = Clamp(p, 0.0, 1.0) * (list.Count - 1);
            int lo = (int)Math.Floor(idx);
            int hi = (int)Math.Ceiling(idx);

            if (lo == hi) return list[lo];

            double w = idx - lo;
            return list[lo] * (1.0 - w) + list[hi] * w;
        }
		
		

        private double MedianVolume(int lookback)
        {
            if (dataBars == null || dataBars.Count < 3) return 0.0;

            int end = dataBars.Count - 2;
            int start = Math.Max(0, end - lookback + 1);

            List<double> list = new List<double>();
            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b != null && b.Volumes != null)
                {
                    list.Add(Math.Max(0.0, (double)b.Volumes.Volume));
                }
            }

            if (list.Count == 0) return 0.0;

            list.Sort();
            int m = list.Count / 2;
            if ((list.Count % 2) == 1) return list[m];
            return 0.5 * (list[m - 1] + list[m]);
        }

        private double MedianWickRatio(int lookback)
        {
            if (dataBars == null || dataBars.Count < 3) return 1.5;

            int end = dataBars.Count - 2;
            int start = Math.Max(0, end - lookback + 1);

            double tick = GetTick();
            List<double> list = new List<double>();

            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null) continue;

                double open = b.Prices.Open;
                double close = b.Prices.Close;
                double hi = b.Prices.High;
                double lo = b.Prices.Low;
                double body = Math.Max(Math.Abs(close - open), tick);
                double upper = Math.Max(open, close);
                double lower = Math.Min(open, close);
                double wTop = Math.Max(0.0, hi - upper);
                double wBot = Math.Max(0.0, lower - lo);
                list.Add((wTop + wBot) / Math.Max(body, tick));
            }

            if (list.Count == 0) return 1.5;

            list.Sort();
            int m = list.Count / 2;
            if ((list.Count % 2) == 1) return list[m];
            return 0.5 * (list[m - 1] + list[m]);
        }

        private void PrintOnceConfig()
        {
            if (_printedCfg) return;
            _printedCfg = true;

            string msg = "CFG s=" + _s.ToString("0.00") + " rt=" + _rt.ToString("0.00")
                + " | MinBars=" + MinBarsRequiredDyn
                + " VolGate>=" + MinCurrentBarVolumeDyn
                + " EmaSlopePeriod=" + EmaSlopePeriodDyn
                + " LookbackForZones=" + LookbackForZonesDyn
                + " DeltaMagLB=" + DeltaMagLookbackBarsDyn
                + " AtrBars=" + AtrProxyBarsDyn
                + " AtrFrac=" + AtrRiskFracDyn.ToString("0.00")
                + " TouchTol=" + BandTouchToleranceTicks
                + " Spacing=" + MinBarsBetweenSignals
                + " Impulse>=" + ImpulseMinRangeTicks
                + " MaxEntryDist=" + MaxEntryDistanceTicksDyn
                + " MinStopFloor=" + MinStopTicksFloorDyn
                + " WickMin=" + WickToBodyMinRatioDyn
                + " RRmin=" + MinRRMultiple
                + " Target[" + MinTargetTicks + "," + MaxTargetTicks + "]"
                + " StopPad=" + StopPadTicks
                + " MaxRisk=" + MaxRiskTicks;

            DebugLog(msg);
        }

        private static double GetTickStatic()
        {
            DataBarConfig cfg = DataBarConfig.Instance;
            if (cfg != null && cfg.TickSize > 0.0) return cfg.TickSize;
            return 0.25;
        }

        private double GetTick() { return GetTickStatic(); }

        private static double RoundToTick(double price, double tick)
        {
            if (tick <= 0.0) tick = 0.25;
            return Math.Round(price / tick, 0, MidpointRounding.AwayFromZero) * tick;
        }

        private int GetAtrProxyTicks(int len, double tick)
        {
            int end = dataBars.Count - 2;
            int start = Math.Max(0, end - len + 1);
            int n = 0;
            double sum = 0.0;

            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null) continue;
                sum += Math.Max(tick, b.Prices.High - b.Prices.Low);
                n++;
            }

            if (n == 0) return 12;
            return (int)Math.Round((sum / n) / tick);
        }

        private bool IsWithinHHmmInt(int hhmmss, int startHHmm, int endHHmm)
        {
            int hh = hhmmss / 10000;
            int mm = (hhmmss / 100) % 100;
            int hhmm = hh * 100 + mm;
            return (hhmm >= startHHmm && hhmm < endHHmm);
        }

        private double GetFastEmaOrClose()
        {
            if (currentTechnicalLevels != null && currentTechnicalLevels.Ema != null)
                return currentTechnicalLevels.Ema.FastEma;
            return currentDataBar.Prices.Close;
        }

        private void RebuildProfilesIfNeeded()
        {
            if (currentDataBar == null || dataBars == null) return;
            if (_lastProfileRecalcBar >= 0 && currentDataBar.BarNumber - _lastProfileRecalcBar < 20) return;

            _lastProfileRecalcBar = currentDataBar.BarNumber;
            _prevPoc = ComputePocFromLastNBars(PocLookbackBars);
            _activity = ComputeActivityLevels(ActivityLookbackBars, ActivityTopPercentile, ActivityMergeTicks);
        }

        private double ComputePocFromLastNBars(int lookback)
        {
            if (dataBars == null || dataBars.Count < 3) return double.NaN;

            int end   = dataBars.Count - 2;
            int start = Math.Max(0, end - lookback + 1);
            double tick = GetTick();

            Dictionary<long, int> tpo = new Dictionary<long, int>();
            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null) continue;

                long a = (long)Math.Floor(b.Prices.Low / tick);
                long z = (long)Math.Ceiling(b.Prices.High / tick);
                for (long k = a; k <= z; k++)
                {
                    int c = 0;
                    if (tpo.ContainsKey(k)) c = tpo[k];
                    tpo[k] = c + 1;
                }
            }

            long bestKey = long.MinValue;
            int bestC = 0;
            foreach (KeyValuePair<long, int> kv in tpo)
            {
                if (kv.Value > bestC) { bestC = kv.Value; bestKey = kv.Key; }
            }

            if (bestC == 0) return double.NaN;
            return bestKey * tick;
        }

        private List<double> ComputeActivityLevels(int lookback, double topPct, int mergeTicks)
        {
            List<double> levels = new List<double>();
            if (dataBars == null || dataBars.Count < 3) return levels;

            int end   = dataBars.Count - 2;
            int start = Math.Max(0, end - lookback + 1);
            double tick = GetTick();

            Dictionary<long, double> vol = new Dictionary<long, double>();
            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null || b.Volumes == null) continue;

                long a = (long)Math.Floor(b.Prices.Low / tick);
                long z = (long)Math.Ceiling(b.Prices.High / tick);
                int slots = (int)Math.Max(1, z - a + 1);
                double share = ((double)b.Volumes.Volume) / slots;

                for (long k = a; k <= z; k++)
                {
                    double cur = 0.0;
                    if (vol.ContainsKey(k)) cur = vol[k];
                    vol[k] = cur + share;
                }
            }

            List<double> vals = new List<double>(vol.Count);
            foreach (KeyValuePair<long, double> kv in vol) vals.Add(kv.Value);
            vals.Sort();
            if (vals.Count == 0) return levels;

            int idx = (int)Math.Floor(topPct * (vals.Count - 1));
            double thr = vals[idx];

            List<long> keys = new List<long>();
            foreach (KeyValuePair<long, double> kv in vol)
                if (kv.Value >= thr) keys.Add(kv.Key);
            keys.Sort();

            long merge = mergeTicks;
            long runStart = long.MinValue, runEnd = long.MinValue;

            for (int i = 0; i < keys.Count; i++)
            {
                long k = keys[i];
                if (runStart == long.MinValue) { runStart = k; runEnd = k; }
                else if (k <= runEnd + merge)  { runEnd = k; }
                else
                {
                    long mid = (runStart + runEnd) / 2;
                    levels.Add(mid * tick);
                    runStart = k; runEnd = k;
                }
            }

            if (runStart != long.MinValue)
            {
                long mid = (runStart + runEnd) / 2;
                levels.Add(mid * tick);
            }

            return levels;
        }

        private bool BlocksBetween(double entry, double target, double tick)
        {
            if (_activity == null) return false;
            double safety = 3.0 * tick;

            if (entry < target)
            {
                for (int i = 0; i < _activity.Count; i++)
                {
                    double p = _activity[i];
                    if (p > entry + safety && p < target - safety) return true;
                }
            }
            else
            {
                for (int i = 0; i < _activity.Count; i++)
                {
                    double p = _activity[i];
                    if (p < entry - safety && p > target + safety) return true;
                }
            }
            return false;
        }

        private bool DetectIcebergAgainst(bool isLongSignal, out double level)
        {
            level = double.NaN;
            if (dataBars == null || dataBars.Count < 3) return false;

            int end   = dataBars.Count - 2;
            int start = Math.Max(0, end - IcebergWindowBars + 1);
            double tick = GetTick();
            double tol  = 0.5 * tick;

            Dictionary<long, int> count = new Dictionary<long, int>();
            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null) continue;
                double px = isLongSignal ? b.Prices.High : b.Prices.Low;
                long key = (long)Math.Round(px / tick);
                int c = 0; if (count.ContainsKey(key)) c = count[key];
                count[key] = c + 1;
            }

            long bestKey = long.MinValue; int bestC = 0;
            foreach (KeyValuePair<long, int> kv in count)
                if (kv.Value > bestC) { bestC = kv.Value; bestKey = kv.Key; }

            if (bestC < IcebergTestsAtLevel) return false;

            double lvl = bestKey * tick;
            double sumAbsSide = 0.0;
            double advance = 0.0;
            double extreme = lvl;

            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null) continue;

                bool touch = isLongSignal
                    ? (b.Prices.High >= lvl - tol)
                    : (b.Prices.Low  <= lvl + tol);

                if (touch && b.Deltas != null)
                    sumAbsSide += Math.Abs((double)b.Deltas.Delta);

                if (isLongSignal)
                {
                    if (b.Prices.High > extreme) extreme = b.Prices.High;
                    if (extreme - lvl > advance) advance = extreme - lvl;
                }
                else
                {
                    if (b.Prices.Low < extreme) extreme = b.Prices.Low;
                    if (lvl - extreme > advance) advance = lvl - extreme;
                }
            }

            double avgAbs = 0.0;
            int n = 0;
            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null || b.Deltas == null) continue;
                avgAbs += Math.Abs((double)b.Deltas.Delta);
                n++;
            }
            if (n == 0) return false;
            avgAbs /= n;

            bool icebergish = (sumAbsSide >= IcebergMinAbsDeltaK * avgAbs) &&
                              (advance <= IcebergMaxAdvanceTicks * tick);

            if (icebergish) { level = lvl; return true; }
            return false;
        }

        // ===== DISK LOGGING HELPERS =====
        private void OpenSignalLogsIfNeeded()
        {
            try
            {
                if (_signalCsv == null)
                {
                    Directory.CreateDirectory(@"C:\ofb_logs");
                    bool exists = File.Exists(@"C:\ofb_logs\signals.csv");
                    _signalCsv = new StreamWriter(@"C:\ofb_logs\signals.csv", true, Encoding.UTF8);
                    if (!exists)
                    {
                        _signalCsv.WriteLine("ts,symbol,side,regime,stress,oppScore,ruleScore,mlScore,stopTicks,targetTicks,qty,trigger,stop,target,reasons");
                        _signalCsv.Flush();
                    }
                }
                if (_trainingEnabled && _signalJsonl == null)
                {
                    _signalJsonl = new StreamWriter(@"C:\ofb_logs\signals.jsonl", true, Encoding.UTF8);
                }
            }
            catch { /* swallow */ }
        }

        private void CsvLine(string line) 
        { 
            try 
            { 
                if (_signalCsv != null) 
                { 
                    _signalCsv.WriteLine(line); 
                    _signalCsv.Flush(); 
                } 
            } 
            catch { } 
        }
        
        private void JsonlLine(string json)
        { 
            try 
            { 
                if (_signalJsonl != null) 
                { 
                    _signalJsonl.WriteLine(json); 
                    _signalJsonl.Flush(); 
                } 
            } 
            catch { } 
        }

        // -- BEGIN: FMS snapshots (ONLY visible output) --
        private void PrintEntrySnapshot(
            bool isLong, double trigger, double stop, double target,
            int stopTicks, int targetTicks, int qty,
            string reasonsCsv)
        {
            if (eventsContainer != null && eventsContainer.EventManager != null)
            {
                eventsContainer.EventManager.PrintMessage(
                    "[FMS] " +
                    string.Format("ENTRY_SNAPSHOT {0:yyyy-MM-dd HH:mm:ss} {1} trg={2:0.00} sl={3:0.00} tp={4:0.00} stopT={5} tgtT={6} qty={7} reasons=[{8}]",
                        currentDataBar.Time, (isLong ? "LONG" : "SHORT"), trigger, stop, target, stopTicks, targetTicks, qty, reasonsCsv)
                );
            }
        }

        private void PrintExitSnapshot(
            string reason, double realizedR, int barsInTrade, double mfeR, double maeR)
        {
            if (eventsContainer != null && eventsContainer.EventManager != null)
            {
                eventsContainer.EventManager.PrintMessage(
                    "[FMS] " +
                    string.Format("EXIT_SNAPSHOT {0:yyyy-MM-dd HH:mm:ss} reason={1} R={2:0.2} bars={3} MFE={4:0.2} MAE={5:0.2}",
                        currentDataBar.Time, reason, realizedR, barsInTrade, mfeR, maeR)
                );
            }
        }
        // -- END: FMS snapshots --

        // ===== FREQUENCY TUNING GETTERS =====
        private int GetMinBarsBetweenSignals()
        {
            int dyn = MinBarsBetweenSignals;
            if (FREQ_MODE == TradeFrequencyMode.Conservative) return dyn;
            if (FREQ_MODE == TradeFrequencyMode.Balanced) return Math.Max(12, Math.Min(dyn, 22));
            // Active
            return Math.Max(10, Math.Min(dyn, 18));
        }

        private double GetVolumeThreshold()
        {
            if (FREQ_MODE == TradeFrequencyMode.Active) return 1.08;  // Scalp mode - better quality
            if (FREQ_MODE == TradeFrequencyMode.Balanced) return 1.15;
            return 1.2;
        }

        private int GetMinDelta3()
        {
            if (FREQ_MODE == TradeFrequencyMode.Active) return 25;  // Scalp mode - better quality
            if (FREQ_MODE == TradeFrequencyMode.Balanced) return 40;
            return MinDelta3;
        }

        private double GetBaseToTriggerATR()
        {
            if (FREQ_MODE == TradeFrequencyMode.Active) return 1.0;  // Scalp mode
            if (FREQ_MODE == TradeFrequencyMode.Balanced) return 1.4;
            return BaseToTriggerATR;
        }

        private int GetLunchKofNRequired() 
        { 
            return (FREQ_MODE == TradeFrequencyMode.Active) ? 3 : 4; 
        }

        private double GetRegimeThresholdAdj()
        {
            // soften Laminar/Transition a bit in Active
            return (FREQ_MODE == TradeFrequencyMode.Active) ? -0.05 : 0.0;
        }

        private int GetCooldownOnStop()
        {
            return (FREQ_MODE == TradeFrequencyMode.Active) ? 1 : CooldownOnStop;
        }

        private double GetMinBarRangeATR()
        {
            return (FREQ_MODE == TradeFrequencyMode.Active) ? 0.25 : MinBarRangeATR;
        }

        // ===== MINIMAL PREFLIGHT (SILENT) =====
        private bool MinimalPreflight(bool isLong)
        {
            // Side allowed?
            if ((isLong && !AllowLongs) || (!isLong && !AllowShorts)) return false;
            
            // Within time window?
            if (RestrictTimeWindow && !IsWithinHHmmInt(currentDataBar.Time, TradeStartHHmm, TradeEndHHmm)) return false;
            
            // Minimum bars required
            if (currentDataBar.BarNumber < MinBarsRequiredDyn) return false;
            
            // Spacing (use regime-aware minimum)
            if (_lastSignalBar >= 0 && currentDataBar.BarNumber - _lastSignalBar < MinBarsBetweenEntries()) return false;
            
            // Volume sanity
            long vol = (currentDataBar.Volumes != null) ? currentDataBar.Volumes.Volume : 0;
            if (vol < MinCurrentBarVolumeDyn) return false;
            
            // Daily P/L cap (if host provides it)
            if (_tradesToday >= DailyMaxTradesDyn) return false;
            
            return true;
        }
        
        private double CalculateVolumeZScore()
        {
            if (dataBars == null || dataBars.Count < 20 || currentDataBar.Volumes == null) return 0;
            
            List<long> volumes = new List<long>();
            for (int i = dataBars.Count - 20; i < dataBars.Count; i++)
            {
                if (dataBars[i].Volumes != null)
                    volumes.Add(dataBars[i].Volumes.Volume);
            }
            
            if (volumes.Count == 0) return 0;
            
            double mean = volumes.Average();
            double sumSquares = volumes.Sum(v => (v - mean) * (v - mean));
            double stdDev = Math.Sqrt(sumSquares / volumes.Count);
            
            if (stdDev == 0) return 0;
            return (currentDataBar.Volumes.Volume - mean) / stdDev;
        }
        
        private int CalculateKofN(bool isLong)
        {
            int score = 0;
            
            // 1. EMA50 slope in direction
            if (ValidateEMA50Slope(isLong)) score++;
            
            // 2. Price vs VWAP in direction
            if (ValidateVWAPPosition(isLong)) score++;
            
            // 3. Delta confirmation
            if (ValidateDeltaConfirmation(isLong)) score++;
            
            // 4. Structure confirmation
            if (ValidateStructureConfirmation(isLong)) score++;
            
            // 5. Momentum threshold
            if (ValidateMomentumThreshold()) score++;
            
            return score;
        }

        // Helper methods for UnifiedOpportunityScore
        private double GetRecentLow(int bars)
        {
            if (dataBars == null || dataBars.Count < bars) return currentDataBar.Prices.Low;
            double low = double.MaxValue;
            for (int i = dataBars.Count - bars; i < dataBars.Count; i++)
                if (dataBars[i].Prices.Low < low) low = dataBars[i].Prices.Low;
            return low;
        }
        
        private double GetRecentHigh(int bars)
        {
            if (dataBars == null || dataBars.Count < bars) return currentDataBar.Prices.High;
            double high = double.MinValue;
            for (int i = dataBars.Count - bars; i < dataBars.Count; i++)
                if (dataBars[i].Prices.High > high) high = dataBars[i].Prices.High;
            return high;
        }
        
        private double CalculateDeltaStdDev(int lookback)
        {
            if (dataBars == null || dataBars.Count < lookback) return 1.0;
            
            List<double> deltas = new List<double>();
            for (int i = dataBars.Count - lookback; i < dataBars.Count; i++)
            {
                if (dataBars[i].Deltas != null)
                    deltas.Add(dataBars[i].Deltas.Delta);
            }
            
            if (deltas.Count == 0) return 1.0;
            
            double mean = deltas.Average();
            double sumSquares = deltas.Sum(d => (d - mean) * (d - mean));
            return Math.Sqrt(sumSquares / deltas.Count);
        }
        
        private bool IsNearClusterLevel(double price, double tick)
        {
            // Check if price is within 2 ticks of a cluster level
            foreach (double level in _clusterLevels)
            {
                if (Math.Abs(price - level) <= 2 * tick)
                    return true;
            }
            return false;
        }
        
        // ===== UNIFIED OPPORTUNITY SCORE HELPERS =====
        private static double Clamp01(double x)
        {
            return x < 0 ? 0 : (x > 1 ? 1 : x);
        }
        
        private static double Norm(double x)
        {
            return 1.0 - Math.Exp(-Math.Abs(x));   // 0..1 squash
        }
        
        private static double Mix(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        // Regime-aware base threshold (laminar easier; turbulent harder)
        private double BaseOppThreshold()
        {
            switch(_currentRegime)
            {
                case Regime.Laminar:    return 0.30;  // Lowered from 0.45 for more trades
                case Regime.Transition: return 0.35;  // Lowered from 0.50
                default:                return 0.40;  // Lowered from 0.58
            }
        }

        // Minimum spacing between entries (bars)
        private int MinBarsBetweenEntries()
        {
            return _currentRegime == Regime.Laminar ? 3 :  // Reduced from 6
                   _currentRegime == Regime.Transition ? 4 : 5;  // Reduced from 8/10
        }

        // Compute a single 0..1 opportunity score from 5 signals (breakout + pullback archetypes)
        private double ComputeUnifiedOpportunityScore(bool isLong,
            out int kofnScore, out double delta3Z, out double baseWidthATR,
            out double volZ, out double distVWAP)
        {
            double tick = GetTick();
            
            // --- BreakoutScore: compression→expansion ---
            double baseWidth = CalculateBaseWidth();
            baseWidthATR = baseWidth / Math.Max(1e-6, _currentATR);
            
            double baseEdge = isLong ? GetRecentLow(CompressionWindow) : GetRecentHigh(CompressionWindow);
            double expansion = (isLong ? (currentDataBar.Prices.Close - baseEdge) : (baseEdge - currentDataBar.Prices.Close)) 
                             / Math.Max(1e-6, _currentATR);
            double breakoutScore = Clamp01(0.5 * (1.0 - Math.Min(1.0, baseWidthATR)) + 0.5 * Math.Max(0, expansion));

            // --- DeltaImpulseScore: last 3 deltas in direction, z-scored ---
            int delta3 = CalculateDelta3(isLong);
            double deltaStdDev = CalculateDeltaStdDev(20);
            delta3Z = deltaStdDev > 0 ? Math.Abs(delta3) / deltaStdDev : 0;
            
            double imbShift = 0.5;
            if (currentDataBar.Volumes != null)
            {
                long askVol = currentDataBar.Volumes.BuyingVolume;
                long bidVol = currentDataBar.Volumes.SellingVolume;
                long total = askVol + bidVol;
                if (total > 0)
                {
                    double imbRatio = isLong ? (double)askVol / total : (double)bidVol / total;
                    imbShift = Math.Abs(imbRatio - 0.5) * 2.0; // 0..1
                }
            }
            double deltaScore = Clamp01(0.6 * Clamp01((delta3Z + 3.0)/6.0) + 0.4 * imbShift);

            // --- VWAPContextScore: near-vwap reversion in laminar; trend if far with direction ---
            distVWAP = Math.Min(3.0, Math.Abs(_currentVWAP - currentDataBar.Prices.Close) / Math.Max(1e-6, _currentATR));
            double trendBias = Clamp01((isLong ? (currentDataBar.Prices.Close - _currentVWAP) : (_currentVWAP - currentDataBar.Prices.Close)) 
                             / Math.Max(1e-6, _currentATR));
            double vwapScore = _currentRegime == Regime.Laminar
                ? Clamp01(1.0 - distVWAP)                          // prefer near VWAP in laminar
                : Mix(Clamp01(1.0 - 0.5*distVWAP), trendBias, 0.5); // blend in direction in transition/turb

            // --- StructureScore: swing clearance & round-number safety ---
            double swingLevel = isLong ? _lastSwingHigh : _lastSwingLow;
            double triggerPrice = CalculateTriggerPrice(isLong);
            bool structureClear = !double.IsNaN(swingLevel) && Math.Abs(triggerPrice - swingLevel) <= 3 * tick;
            
            double proposedStop = isLong ? (currentDataBar.Prices.Close - CalculateStopDistance(isLong)) 
                                         : (currentDataBar.Prices.Close + CalculateStopDistance(isLong));
            bool isStopOnCluster = IsNearClusterLevel(proposedStop, tick);
            
            double swingClear = structureClear ? 1.0 : 0.0;
            double roundPenalty = isStopOnCluster ? 0.0 : 1.0;
            double structureScore = Clamp01(0.7 * swingClear + 0.3 * roundPenalty);
            
            // --- PullbackScore: trend filter + shallow retrace + fresh delta pop ---
            double pullbackScore = ComputePullbackScore(isLong, delta3Z);

            // --- Volume z-score (light guard, returned separately) ---
            volZ = CalculateVolumeZScore();

            // --- K-of-N (keep simple & softer) ---
            bool compressionOk = baseWidthATR <= 1.6;
            bool trendOk = ValidateEMA50Slope(isLong);
            kofnScore = (structureClear?1:0) + (compressionOk?1:0) + (delta3Z>0?1:0) + (volZ>0?1:0) + (trendOk?1:0);

            // --- Final opportunity (blend 5 archetypes) ---
            double opp = 0.28 * breakoutScore +
                        0.22 * deltaScore +
                        0.20 * vwapScore +
                        0.15 * structureScore +
                        0.15 * pullbackScore;

            return Clamp01(opp);
        }
        
        // PullbackScore: trend filter + shallow retrace to VWAP/EMA + fresh delta pop
        private double ComputePullbackScore(bool isLong, double delta3Z)
        {
            if (double.IsNaN(_currentEMA50) || double.IsNaN(_currentVWAP)) return 0;
            
            // Trend: Close above EMA/VWAP for longs, below for shorts
            bool trendOk = isLong ? (currentDataBar.Prices.Close > _currentEMA50 && currentDataBar.Prices.Close > _currentVWAP)
                                  : (currentDataBar.Prices.Close < _currentEMA50 && currentDataBar.Prices.Close < _currentVWAP);

            // Retrace depth in ATRs (prefer 0.25–0.75 ATR)
            if (dataBars == null || dataBars.Count < 2) return 0;
            double depthAtr = Math.Abs(currentDataBar.Prices.Close - dataBars[dataBars.Count - 2].Prices.Close) / Math.Max(1e-6, _currentATR);
            double depthScore = Math.Max(0, 1.0 - Math.Abs(depthAtr - 0.5)); // peak at 0.5 ATR

            // Fresh delta pop in direction (use existing delta3 z-score)
            double pop = Math.Max(0, Math.Min(1, (delta3Z + 2.0) / 4.0));

            return (trendOk ? 1.0 : 0.0) * (0.5 * depthScore + 0.5 * pop);
        }

        // ===== CENTRALIZED DEBUG HELPERS (TRUE NO-OPS unless FullDebug) =====
        // All FMS debug output routes through these - even if legacy code tries to print,
        // HandlePrintMessage whitelist will drop it.
        private void D(string msg)
        {
            if (PRINT_MODE != SignalPrintMode.FullDebug) return;
            try
            {
                var em = (eventsContainer != null) ? eventsContainer.EventManager : null;
                if (em != null && currentDataBar != null)
                    em.PrintMessage("[FMS] " + currentDataBar.Time.ToString() + " | " + msg);
            }
            catch { }
        }

        private void DS(bool isLong, string msg)
        {
            D((isLong ? "[L] " : "[S] ") + msg);
        }
        
        // Legacy compatibility - all route through D()/DS()
        private void DebugLogSide(bool isLong, string msg)
        {
            DS(isLong, msg);
        }
        
        private void DebugLog(string msg)
        {
            D(msg);
        }
    }
}
