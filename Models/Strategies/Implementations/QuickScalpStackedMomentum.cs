using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Containers;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.DataBars;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.TechnicalLevelsModel;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies;
using System;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies.Implementations
{
    /// <summary>
    /// Quick scalp idea for ES/MES.
    /// Long: price above fast EMA, bar is bullish, positive delta.
    /// Short: symmetric. Uses StrategyBase so OFB can place the ATM order.
    /// </summary>
    public class QuickScalpStackedMomentum : StrategyBase
    {
        // Tunables
        private readonly int  _minBars     = 5;
        private readonly int  _minVolume   = 1000;   // gate on current bar volume
        private readonly long _minDelta    = 300;   // sign + magnitude gate
        private readonly long _strongDelta = 800;   // skip EMA gate if delta is very strong

        // Prevent multiple signals on same bar
        private int _lastSignalBar = -1;

        // Toggle verbose prints
        private const bool DEBUG = true;

        public QuickScalpStackedMomentum(EventsContainer eventsContainer) : base(eventsContainer)
        {
            StrategyData.Name = "Quick Scalp (6-8-5)";
        }

        public override bool CheckLong()
        {
            return TrySignal(true);
        }

        public override bool CheckShort()
        {
            return TrySignal(false);
        }

        private bool TrySignal(bool isLong)
        {
            // Ensure we have enough context
            if (dataBars == null || dataBars.Count < _minBars || currentDataBar == null)
                return false;

            // One signal per bar
            if (currentDataBar.BarNumber == _lastSignalBar)
                return false;

            // Trigger Strike Price filter
            if (!IsValidTriggerStrikePrice())
            {
                Debug("Blocked by Trigger Strike Price filter");
                return false;
            }
			
			if (!HasAtrRange(4))  // min 1 point range instead of 1.5
			{
			    Debug("ATR range too small, skipping.");
			    return false;
			}

            // Delta gate
            long d = currentDataBar.Deltas.Delta;
            if (isLong && d < _minDelta)
            {
                Debug("Delta gate (Long): " + d + " < " + _minDelta);
                return false;
            }
            if (!isLong && d > -_minDelta)
            {
                Debug("Delta gate (Short): " + d + " > -" + _minDelta);
                return false;
            }
			
			if (!HasDeltaTrend(isLong, 2)) return false;
			if (!HasMomentumTrend(isLong, 2)) return false;

            // EMA gate (safe syntax, no ?. operator)
            double fastEma = currentDataBar.Prices.Close;
            if (currentTechnicalLevels != null && currentTechnicalLevels.Ema != null)
                fastEma = currentTechnicalLevels.Ema.FastEma;

            bool emaOk = isLong
                ? (currentDataBar.Prices.Close > fastEma)
                : (currentDataBar.Prices.Close < fastEma);

            if (!emaOk && Math.Abs(d) < _strongDelta)
            {
                Debug("EMA gate failed: Close=" + currentDataBar.Prices.Close.ToString("0.00") +
                      " vs FastEMA=" + fastEma.ToString("0.00"));
                return false;
            }

            // ✅ All simple conditions passed → signal
            StrategyData.UpdateTriggeredDataProvider(
                isLong ? Direction.Long : Direction.Short,
                true
            );

            _lastSignalBar = currentDataBar.BarNumber;

            Debug("SIGNAL " + (isLong ? "LONG" : "SHORT") +
                  " | Close=" + currentDataBar.Prices.Close.ToString("0.00") +
                  " EMA=" + fastEma.ToString("0.00") +
                  " Delta=" + d);

            return true;
        }

        private void Debug(string msg)
        {
            if (!DEBUG) return;
            if (eventsContainer != null && eventsContainer.EventManager != null)
                eventsContainer.EventManager.PrintMessage("[QuickScalp] " +
                    (currentDataBar != null ? currentDataBar.Time.ToString() : "") +
                    " | " + msg);
        }

        private bool HasMomentumTrend(bool isLong, int lookback = 3)
        {
            if (dataBars.Count < lookback + 1) return true; // don’t block at session start

            for (int i = dataBars.Count - lookback; i < dataBars.Count; i++)
            {
                var bar = dataBars[i];
                if (isLong && bar.BarType != BarType.Bullish) return false;
                if (!isLong && bar.BarType != BarType.Bearish) return false;
            }
            return true;
        }

        private bool HasDeltaTrend(bool isLong, int lookback = 3)
        {
            if (dataBars.Count < lookback + 1) return false;

            long sum = 0;
            for (int i = dataBars.Count - lookback; i < dataBars.Count; i++)
                sum += dataBars[i].Deltas.Delta;

            return isLong ? sum > 0 : sum < 0;
        }

        private bool HasRelativeVolume(int lookback = 10, double multiplier = 1.2)
        {
            if (dataBars.Count < lookback) return false;

            double avg = 0;
            for (int i = dataBars.Count - lookback; i < dataBars.Count; i++)
                avg += dataBars[i].Volumes.Volume;
            avg /= lookback;

            return currentDataBar.Volumes.Volume > avg * multiplier;
        }

        private bool HasAtrRange(double minTicks = 6)
        {
            // Use instrument’s tick size from Configs or DataBar
            double tickSize = DataBarConfig.Instance.TickSize > 0
                ? DataBarConfig.Instance.TickSize
                : 0.25; // fallback for ES

            double range = currentDataBar.Prices.High - currentDataBar.Prices.Low;
            return range >= minTicks * tickSize;
        }
    }
}
