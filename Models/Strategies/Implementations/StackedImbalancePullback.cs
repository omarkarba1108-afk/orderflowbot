using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Containers;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.DataBars;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies;
using System;
using System.Collections.Generic;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies.Implementations
{
    /// <summary>
    /// Stacked Imbalance Pullback (Filtered, Managed exits) — v1 tuned
    /// - Zones from stacked imbalances (Ask-stacked = support, Bid-stacked = resistance)
    /// - Impulse away before first pullback
    /// - EMA bias + EMA slope filter
    /// - Delta sign + magnitude OR wick confirmation
    /// - Dynamic SL/TP with RR floor; room-to-target enforced
    /// - Per-level cooldown, zone freshness, daily trade cap (HHmmss)
    /// - Micro-confirmation uses CURRENT bar > prior bar extreme (+/- ticks)
    /// </summary>
    public class StackedImbalancePullback : StrategyBase
    {
        // -------- Tunables (focused changes) --------
        private const bool   AllowLongs               = true;
        private const bool   AllowShorts              = false;     // disable for now; re-enable after stats improve

        private const int    MinBarsRequired          = 15;
        private const int    LookbackForZones         = 200;
        private const int    MaxZoneAgeBars           = 140;
        private const double ProximityTicks           = 3.0;       // tighter retest proximity
        private const int    MinBarsBetweenSignals    = 8;
        private const long   MinCurrentBarVolume      = 200L;      // a touch stricter

        // Confirmation
        private const bool   RequireDeltaConfirm      = true;      // require direction of delta
        private const bool   UseDeltaMagnitude        = true;      // NEW: also require size
        private const double DeltaMagK                = 0.5;      // relative to avg |delta| of last N bars
        private const int    DeltaMagLookbackBars     = 25;
        private const double WickToBodyMinRatio       = 1.15;      // slightly softer than 1.30

        // Trend / bias
        private const bool   UseEmaBias               = true;
        private const double EmaToleranceTicks        = 1.25;

        // Impulse filter
        private const int    ImpulseLookbackBars      = 6;
        private const double ImpulseRangeTicks        = 6;        // require a bit more punch

        // Room-to-target coarse gate (now enforced)
        private const int    AssumedTargetTicks       = 10;
        private const int    FrontRunTicks            = 3;
        private const bool   EnforceRoomToTarget      = false;

        // Dynamic exits
        private const int    StopBufferTicks          = 4;         // slightly tighter baseline pad
        private const int    MinStopTicksFloor        = 8;         // absolute floor
        private const int    MinTargetTicks           = 8;
        private const int    MaxTargetTicks           = 24;        // tighter cap
        private const double MinRRMultiple            = 1.20;      // stricter RR floor
        private const int    MaxRiskTicks             = 14;        // reject trades that bloat risk

        // Trend strength gate (EMA slope)
        private const int    EmaSlopePeriod           = 50;
        private const double MinSlopeTicksPerBar      = 0.45;      // slightly higher than 0.50

        // Per-level cooldown and freshness
        private const int    PerLevelCooldownBars     = 20;        // reduce re-trade of same level
        private const int    MaxBarsSinceZoneCreated  = 140;        // prefer fresher zones

        private const int    MaxEntryDistanceTicks    = 3;         // don’t chase entries

        // Daily trade cap (using HHmmss wrap)
        private const int    DailyTradeCap            = 200;        // reduce over-trading

        private const bool   DEBUG                    = true;

        // Stop robustness (kept for future tuning)
        private const int    MinStopTicks             = 10;
        private const int    AtrLenBars               = 20;
        private const double AtrMult                  = 0.90;
		private const double AtrRiskFrac              = 0.30;

        private const bool   RestrictTimeWindow       = true;
        private const int    TradeStartHHmm           = 1400;      // 14:00
        private const int    TradeEndHHmm             = 1700;      // 17:00 (exclusive)

        // Vol-adaptive min risk calc (used below)
       
        // ---------------------------------------------
        private class Zone
        {
            public double Price;
            public bool   IsLongZone;
            public int    CreatedBarNumber;
            public bool   Consumed;
        }

        private readonly List<Zone> _zones = new List<Zone>();
        private int _lastSignalBar  = -1;
        private double _lastZonePrice = double.NaN;
        private int _lastZoneBar    = -1;

        private int _tradesToday    = 0;
        private int _lastBarTimeInt = -1;   // store last HHmmss to detect day/session wrap

        private bool _printedCfg = false;

        public StackedImbalancePullback(EventsContainer eventsContainer) : base(eventsContainer)
        {
            StrategyData.Name = "Stacked Imbalance Pullback (Filtered)";
        }

        public override bool CheckLong()  { return TrySignal(true); }
        public override bool CheckShort() { return TrySignal(false); }

        // ----------------- Core -----------------
        private bool TrySignal(bool isLong)
        {
            PrintOnceConfig();

            if (dataBars == null || currentDataBar == null || dataBars.Count < MinBarsRequired)
            {
                DebugLog("Gate: not enough bars.");
                return false;
            }

            // Time window gate
            if (RestrictTimeWindow)
            {
                if (!IsWithinHHmmInt(currentDataBar.Time, TradeStartHHmm, TradeEndHHmm))
                {
                    if (DEBUG) DebugLog("Gate: outside time window.");
                    return false;
                }
            }

            // Daily cap by HHmmss wrap
            ResetDailyIfNewSession();
            if (_tradesToday >= DailyTradeCap)
            {
                if (DEBUG) DebugLog("Gate: daily trade cap hit (" + _tradesToday + "/" + DailyTradeCap + ")");
                return false;
            }

            if ((isLong && !AllowLongs) || (!isLong && !AllowShorts))
            {
                DebugLog("Gate: direction not allowed.");
                return false;
            }

            if (currentDataBar.BarNumber == _lastSignalBar)
            {
                DebugLog("Gate: one signal per bar.");
                return false;
            }
            if (_lastSignalBar > 0 && currentDataBar.BarNumber - _lastSignalBar < MinBarsBetweenSignals)
            {
                DebugLog("Gate: global cooldown.");
                return false;
            }

            long curVol = (currentDataBar.Volumes != null) ? currentDataBar.Volumes.Volume : 0L;
            if (curVol < MinCurrentBarVolume)
            {
                DebugLog("Gate: volume " + curVol + " < " + MinCurrentBarVolume);
                return false;
            }

            double tickSize = (DataBarConfig.Instance != null && DataBarConfig.Instance.TickSize > 0)
                                ? DataBarConfig.Instance.TickSize : 0.25;

            if (UseEmaBias)
            {
                double ema = GetFastEmaOrClose();
                double tol = EmaToleranceTicks * tickSize;
                bool ok = isLong ? currentDataBar.Prices.Close >= ema - tol
                                 : currentDataBar.Prices.Close <= ema + tol;
                if (!ok)
                {
                    DebugLog("Gate: EMA bias fail. close=" + currentDataBar.Prices.Close.ToString("0.00") +
                             " ema=" + ema.ToString("0.00"));
                    return false;
                }
            }

            // Trend-strength filter
            if (!EmaSlopeOk(isLong))
            {
                DebugLog("Gate: EMA slope weak.");
                return false;
            }

            ScanForNewZones();
            PurgeOldZones();

            double tolRetest = ProximityTicks * tickSize;
            Zone hit = FindHitZone(isLong, currentDataBar, tolRetest);
            if (hit == null)
            {
                DebugLog("Gate: no matching zone retest.");
                return false;
            }

            // Freshness
            int barsSinceCreated = currentDataBar.BarNumber - hit.CreatedBarNumber;
            if (barsSinceCreated > MaxBarsSinceZoneCreated)
            {
                DebugLog("Gate: zone stale.");
                return false;
            }

            // Per-level cooldown
            bool sameLevelRecently =
                !double.IsNaN(_lastZonePrice) &&
                Math.Abs(hit.Price - _lastZonePrice) <= (2.0 * tickSize) &&
                currentDataBar.BarNumber - _lastZoneBar < PerLevelCooldownBars;
            if (sameLevelRecently)
            {
                DebugLog("Gate: same level recently traded.");
                return false;
            }

            if (!HasImpulseAwayFromZone(isLong, hit.Price, ImpulseLookbackBars, ImpulseRangeTicks * tickSize))
            {
                DebugLog("Gate: no impulse away.");
                return false;
            }

            // Bar-quality gate (avoid chasing the bar)
            double hi  = currentDataBar.Prices.High;
            double lo  = currentDataBar.Prices.Low;
            double rng = Math.Max(tickSize, hi - lo);
            double rngT = rng / tickSize;
            if (rngT < 6) { DebugLog("Gate: small bar."); return false; }

            double close = currentDataBar.Prices.Close;
            double pos   = (close - lo) / rng;  // 0..1
            if (isLong && pos < 0.60)  { DebugLog("Gate: weak bull close."); return false; } // was 0.70
            if (!isLong && pos > 0.40) { DebugLog("Gate: weak bear close."); return false; }

            if (EnforceRoomToTarget && !HasRoomToTarget(isLong, currentDataBar.Prices.Close, tickSize))
            {
                DebugLog("Gate: no room to target (opposing zone too close).");
                return false;
            }

            if (!HasDirectionalConfirm(isLong, currentDataBar, WickToBodyMinRatio, tickSize))
            {
                DebugLog("Gate: confirm failed (delta/wick).");
                return false;
            }

            // --- Micro-confirmation (FIXED: compare CURRENT vs PRIOR bar extreme) ---
			int confirmTicks = 1;
			var prev = dataBars[dataBars.Count - 2];
			bool confirmed = isLong
			    ? (currentDataBar.Prices.High >= prev.Prices.High + confirmTicks * tickSize)
			    : (currentDataBar.Prices.Low  <= prev.Prices.Low  - confirmTicks * tickSize);
			if (!confirmed) { DebugLog("Gate: no micro-confirmation."); return false; }


            // ---- Dynamic exits ----
            DynOrders dyn = ComputeDynamicOrders(isLong, hit.Price, currentDataBar.Prices.Close);
            if (!dyn.IsValid)
            {
                DebugLog("Gate: dynamic TP/SL invalid (no room/RR).");
                return false;
            }

            // ---- Raise Managed proposal to host ----
            string tag   = isLong ? "SIP_Long" : "SIP_Short";
            var shared = (eventsContainer != null && eventsContainer.StrategiesEvents != null)
                ? eventsContainer.StrategiesEvents.StrategyData as StrategyData
                : null;

            if (shared != null)
            {
                shared.UpdateTriggeredDataProvider(
                    isLong ? Direction.Long : Direction.Short,
                    true,
                    dyn.StopPrice,          // absolute price
                    dyn.TargetPrice,        // absolute price
                    tag,
                    EntryExecMode.Managed
                );
            }

            // ---- Consume & account ----
            _lastSignalBar = currentDataBar.BarNumber;
            hit.Consumed   = true;
            _lastZonePrice = hit.Price;
            _lastZoneBar   = currentDataBar.BarNumber;
            _tradesToday++;

            DebugLog("SIGNAL " + (isLong ? "LONG" : "SHORT")
                     + " | Zone=" + hit.Price.ToString("0.00")
                     + " | Stop=" + dyn.StopPrice.ToString("0.00")
                     + " | Target=" + dyn.TargetPrice.ToString("0.00")
                     + " | TradesToday=" + _tradesToday + "/" + DailyTradeCap);

            return true;
        }

        // ----------------- Helpers -----------------
        private static bool IsWithinHHmmInt(int hhmmss, int startHHmm, int endHHmm)
        {
            // hhmmss -> HHmm
            int hh = hhmmss / 10000;
            int mm = (hhmmss / 100) % 100;
            int hhmm = hh * 100 + mm;
            return (hhmm >= startHHmm && hhmm < endHHmm);
        }

        private static bool IsWithinHHmm(DateTime t, int startHHmm, int endHHmm)
        {
            int hhmm = t.Hour * 100 + t.Minute;
            return (hhmm >= startHHmm && hhmm < endHHmm);
        }

        private int GetRecentATRTicks(int len, double tick)
        {
            int count = 0;
            double sum = 0.0;

            int end = dataBars.Count - 1;
            int start = Math.Max(1, end - len + 1);  // need (i-1)

            for (int i = start; i <= end; i++)
            {
                var b  = dataBars[i];
                var bp = dataBars[i - 1];
                if (b == null || bp == null) continue;

                double tr = b.Prices.High - b.Prices.Low;
                sum += Math.Max(tr, tick); // at least 1 tick
                count++;
            }

            if (count == 0 || tick <= 0) return 0;
            double atr = (sum / count) / tick;            // convert to ticks
            return (int)Math.Round(atr);
        }

        private void PrintOnceConfig()
        {
            if (_printedCfg) return;
            _printedCfg = true;
            DebugLog("CFG AllowLongs=" + AllowLongs +
                     " AllowShorts=" + AllowShorts +
                     " UseEmaBias=" + UseEmaBias +
                     " EmaTolTicks=" + EmaToleranceTicks +
                     " MinVol=" + MinCurrentBarVolume +
                     " ProxTicks=" + ProximityTicks +
                     " Impulse=" + ImpulseRangeTicks +
                     " StopBuf=" + StopBufferTicks +
                     " Target[min,max]=" + MinTargetTicks + "," + MaxTargetTicks +
                     " MinRR=" + MinRRMultiple +
                     " SlopeTPB=" + MinSlopeTicksPerBar);
        }

        // EMA slope in ticks/bar
        private bool EmaSlopeOk(bool isLong)
        {
            double emaNow = (currentTechnicalLevels != null && currentTechnicalLevels.Ema != null)
                ? currentTechnicalLevels.Ema.FastEma
                : currentDataBar.Prices.Close;

            int lookback = Math.Max(5, EmaSlopePeriod / 4);
            if (dataBars == null || dataBars.Count < lookback + 2)
                return false;

            double emaPast = dataBars[dataBars.Count - 1 - lookback].Prices.Close;
            double slopePerBar = (emaNow - emaPast) / lookback;

            double tickSize = 0.25;
            if (DataBarConfig.Instance != null && DataBarConfig.Instance.TickSize > 0)
                tickSize = DataBarConfig.Instance.TickSize;

            double ticksPerBar = tickSize > 0.0 ? slopePerBar / tickSize : 0.0;

            if (DEBUG) DebugLog("Slope tpb=" + ticksPerBar.ToString("0.00"));

            return isLong ? (ticksPerBar >= MinSlopeTicksPerBar)
                          : (ticksPerBar <= -MinSlopeTicksPerBar);
        }

        // Round to instrument tick grid without needing Instrument.* here
        private static double RoundToTick(double price)
        {
            double ts = 0.25;
            if (DataBarConfig.Instance != null && DataBarConfig.Instance.TickSize > 0)
                ts = DataBarConfig.Instance.TickSize;

            return Math.Round(price / ts, 0, MidpointRounding.AwayFromZero) * ts;
        }

        private struct DynOrders
        {
            public bool IsValid;
            public double StopPrice;
            public double TargetPrice;
            public int StopTicks;
            public int TargetTicks;
        }

        private DynOrders ComputeDynamicOrders(bool isLong, double zonePrice, double entryPrice)
        {
            double tick = 0.25;
            if (DataBarConfig.Instance != null && DataBarConfig.Instance.TickSize > 0)
                tick = DataBarConfig.Instance.TickSize;

            // --- noise-aware padding using current bar range ---
            double hi  = currentDataBar.Prices.High;
            double lo  = currentDataBar.Prices.Low;
            double rng = Math.Max(tick, hi - lo);
            int rangeTicks = (int)Math.Round(rng / tick);

            int halfRange = rangeTicks / 2;
            int padTicks  = StopBufferTicks;
            if (MinStopTicksFloor > padTicks) padTicks = MinStopTicksFloor;
            if (halfRange        > padTicks) padTicks = halfRange;

            // Stop beyond zone and beyond bar extreme
            double stop = isLong
                ? Math.Min(zonePrice - padTicks * tick, lo - tick)
                : Math.Max(zonePrice + padTicks * tick, hi + tick);

            // Round
            stop = RoundToTick(stop);

            // reject late entries too far from the zone
            int entryDistTicks = (int)Math.Round(Math.Abs(entryPrice - zonePrice) / tick);
            if (entryDistTicks > MaxEntryDistanceTicks)
                return new DynOrders { IsValid = false };

            // Risk in ticks
            int riskTicks = (int)Math.Round(Math.Abs(entryPrice - stop) / tick);
            if (riskTicks < MinStopTicksFloor) riskTicks = MinStopTicksFloor;
            if (riskTicks > MaxRiskTicks)      return new DynOrders { IsValid = false };

            int minRewardTicks = Math.Max(MinTargetTicks, (int)Math.Ceiling(riskTicks * MinRRMultiple));

            // Vol-adjusted minimum risk
            double atr = 0.0;
            try
            {
                int n = Math.Min(10, dataBars != null ? dataBars.Count - 1 : 0);
                if (n > 2)
                {
                    double sum = 0.0;
                    for (int k = dataBars.Count - n - 1; k <= dataBars.Count - 2; k++)
                        sum += (dataBars[k].Prices.High - dataBars[k].Prices.Low);
                    atr = (sum / (double)(n - 1));
                }
            }
            catch { atr = 0.0; }

            int atrTicks = tick > 0 ? (int)Math.Round(atr / tick) : 0;
            int minRiskTicks = Math.Max(MinStopTicksFloor, (int)Math.Ceiling(AtrRiskFrac * Math.Max(atrTicks, 8)));
            if (riskTicks < minRiskTicks)
            {
                if (DEBUG) DebugLog("dyn: RISK_TOO_SMALL_FOR_VOL risk=" + riskTicks + " min=" + minRiskTicks);
                return new DynOrders { IsValid = false };
            }

            // Candidate opposing levels (nearest + next)
            var cands = new List<double>();
            double opp1 = NearestOpposingZone(isLong, entryPrice);
            if (!double.IsNaN(opp1)) cands.Add(opp1);

            if (!double.IsNaN(opp1))
            {
                double ref2 = isLong ? (opp1 + tick) : (opp1 - tick);
                double opp2 = NearestOpposingZone(isLong, ref2);
                if (!double.IsNaN(opp2) && Math.Abs(opp2 - entryPrice) > Math.Abs(opp1 - entryPrice))
                    cands.Add(opp2);
            }

            if (cands.Count == 0) return new DynOrders { IsValid = false };

            for (int i = 0; i < cands.Count; i++)
            {
                double opp = cands[i];

                double target = isLong
                    ? opp - FrontRunTicks * tick
                    : opp + FrontRunTicks * tick;

                int rewardTicks = (int)Math.Round(Math.Abs(target - entryPrice) / tick);

                // If RR too low, extend toward desired RR but cap at MaxTargetTicks
                if (rewardTicks < minRewardTicks)
                {
                    int want = Math.Min(Math.Max(minRewardTicks, rewardTicks), MaxTargetTicks);
                    target      = isLong ? entryPrice + want * tick : entryPrice - want * tick;
                    rewardTicks = want;
                }

                if (rewardTicks > MaxTargetTicks)
                {
                    rewardTicks = MaxTargetTicks;
                    target      = isLong ? entryPrice + rewardTicks * tick : entryPrice - rewardTicks * tick;
                }

                // Correct side & round
                if (isLong && target <= entryPrice + tick)  continue;
                if (!isLong && target >= entryPrice - tick) continue;

                target = RoundToTick(target);

                return new DynOrders
                {
                    IsValid     = true,
                    StopPrice   = stop,
                    TargetPrice = target,
                    StopTicks   = riskTicks,
                    TargetTicks = rewardTicks
                };
            }

            // Fallback to a capped fixed target if nothing made it through
            if (MaxTargetTicks >= MinTargetTicks)
            {
                double fallbackTarget = isLong
                    ? entryPrice + MaxTargetTicks * tick
                    : entryPrice - MaxTargetTicks * tick;

                fallbackTarget = RoundToTick(fallbackTarget);

                return new DynOrders
                {
                    IsValid     = true,
                    StopPrice   = stop,
                    TargetPrice = fallbackTarget,
                    StopTicks   = riskTicks,
                    TargetTicks = MaxTargetTicks
                };
            }

            return new DynOrders { IsValid = false };
        }

        private double NearestOpposingZone(bool isLong, double refPrice)
        {
            double best = double.NaN;

            for (int i = 0; i < _zones.Count; i++)
            {
                Zone z = _zones[i];
                if (z.Consumed) continue;

                if (isLong && !z.IsLongZone && z.Price > refPrice)
                {
                    if (double.IsNaN(best) || z.Price < best) best = z.Price;
                }
                else if (!isLong && z.IsLongZone && z.Price < refPrice)
                {
                    if (double.IsNaN(best) || z.Price > best) best = z.Price;
                }
            }
            return best;
        }

        private void ResetDailyIfNewSession()
        {
            if (currentDataBar == null) return;

            int t = currentDataBar.Time; // HHmmss as int
            if (_lastBarTimeInt >= 0 && t < _lastBarTimeInt)
            {
                _tradesToday = 0;
                if (DEBUG) DebugLog("Daily counters reset (time wrap).");
            }
            _lastBarTimeInt = t;
        }

        private void ScanForNewZones()
        {
            int end = dataBars.Count - 2;    // exclude current building bar
            int start = Math.Max(0, end - LookbackForZones);

            double tickSize = (DataBarConfig.Instance != null && DataBarConfig.Instance.TickSize > 0)
                                ? DataBarConfig.Instance.TickSize : 0.25;
            double mergeTol = 1.0 * tickSize;

            for (int i = start; i <= end; i++)
            {
                IReadOnlyDataBar b = dataBars[i];
                if (b == null || b.Imbalances == null) continue;

                // Mapping: Ask-stacked -> long zone (support), Bid-stacked -> short zone (resistance)
                bool longZone  = b.Imbalances.HasAskStackedImbalances;
                bool shortZone = b.Imbalances.HasBidStackedImbalances;
                if (!longZone && !shortZone) continue;

                double price = b.Prices.Close;
                bool exists = false;
                for (int k = 0; k < _zones.Count; k++)
                {
                    if (Math.Abs(_zones[k].Price - price) <= mergeTol &&
                        _zones[k].IsLongZone == longZone) { exists = true; break; }
                }
                if (!exists)
                {
                    _zones.Add(new Zone
                    {
                        Price = price,
                        IsLongZone = longZone,
                        CreatedBarNumber = b.BarNumber,
                        Consumed = false
                    });
                }
            }
        }

        private void PurgeOldZones()
        {
            int cur = currentDataBar.BarNumber;
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                if (_zones[i].Consumed) { _zones.RemoveAt(i); continue; }
                if (cur - _zones[i].CreatedBarNumber > MaxZoneAgeBars) _zones.RemoveAt(i);
            }
        }

        private Zone FindHitZone(bool isLong, IReadOnlyDataBar bar, double tolRetest)
        {
            Zone hit = null;
            double bestDist = double.MaxValue;

            for (int i = 0; i < _zones.Count; i++)
            {
                Zone z = _zones[i];
                if (z.Consumed || z.IsLongZone != isLong) continue;
                if (bar.BarNumber <= z.CreatedBarNumber) continue; // need at least one bar after creation
                if (bar.BarNumber - z.CreatedBarNumber < 2) continue; // at least 1 full bar after zone

                if (TouchesLevelOnBar(bar, z.Price, tolRetest))
                {
                    double d = Math.Abs(bar.Prices.Close - z.Price);
                    if (d < bestDist) { bestDist = d; hit = z; }
                }
            }
            return hit;
        }

        private static bool TouchesLevelOnBar(IReadOnlyDataBar bar, double price, double tol)
        {
            double hi = bar.Prices.High, lo = bar.Prices.Low;
            if (hi < price - tol) return false;
            if (lo > price + tol) return false;
            return true;
        }

        private bool HasImpulseAwayFromZone(bool isLong, double zonePrice, int lookbackBars, double minRange)
        {
            int end = dataBars.Count - 2; // last completed bar
            int start = Math.Max(0, end - lookbackBars + 1);

            for (int i = end; i >= start; i--)
            {
                var b = dataBars[i];
                if (b == null) continue;

                double range = b.Prices.High - b.Prices.Low;
                if (range < minRange) continue;

                if (isLong && b.Prices.Close > zonePrice) return true;
                if (!isLong && b.Prices.Close < zonePrice) return true;
            }
            return false;
        }

        private bool HasRoomToTarget(bool isLong, double entryPrice, double tickSize)
        {
            double nearestOpp = double.NaN;
            for (int i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                if (z.Consumed) continue;

                if (isLong && !z.IsLongZone && z.Price > entryPrice)
                {
                    if (double.IsNaN(nearestOpp) || z.Price < nearestOpp) nearestOpp = z.Price;
                }
                else if (!isLong && z.IsLongZone && z.Price < entryPrice)
                {
                    if (double.IsNaN(nearestOpp) || z.Price > nearestOpp) nearestOpp = z.Price;
                }
            }

            if (double.IsNaN(nearestOpp)) return true;

            double distTicks = Math.Abs(nearestOpp - entryPrice) / tickSize;
            return distTicks >= (AssumedTargetTicks + FrontRunTicks);
        }

        private bool HasDirectionalConfirm(bool isLong, IReadOnlyDataBar bar, double wickToBodyMin, double tickSize)
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

            double open  = bar.Prices.Open;
            double close = bar.Prices.Close;
            double high  = bar.Prices.High;
            double low   = bar.Prices.Low;
            double body  = Math.Max(Math.Abs(close - open), tickSize);
            double upperBase = Math.Max(open, close);
            double lowerBase = Math.Min(open, close);
            double topWick = Math.Max(0.0, high - upperBase);
            double botWick = Math.Max(0.0, lowerBase - low);
            bool wickOk = (isLong ? botWick : topWick) >= wickToBodyMin * body;

            return deltaOk || wickOk;
        }

        private double DeltaMagnitudeThreshold()
        {
            int end = dataBars.Count - 1;
            int start = Math.Max(0, end - DeltaMagLookbackBars + 1);
            double sum = 0.0; int n = 0;

            for (int i = start; i <= end; i++)
            {
                var b = dataBars[i];
                if (b == null || b.Deltas == null) continue;
                sum += Math.Abs((double)b.Deltas.Delta);
                n++;
            }
            if (n == 0) return double.MaxValue; // force fail if no data
            return (sum / n) * DeltaMagK;
        }

        private double GetFastEmaOrClose()
        {
            if (currentTechnicalLevels != null && currentTechnicalLevels.Ema != null)
                return currentTechnicalLevels.Ema.FastEma;
            return currentDataBar.Prices.Close;
        }

        private void DebugLog(string msg)
        {
            if (!DEBUG) return;
            try
            {
                var em = eventsContainer != null ? eventsContainer.EventManager : null;
                if (em != null)
                    em.PrintMessage("[StackedImbPullback] " +
                        (currentDataBar != null ? currentDataBar.Time.ToString() : "") +
                        " | " + msg);
            }
            catch { }
        }
    }
}
