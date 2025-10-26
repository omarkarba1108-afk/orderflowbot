# OrderFlowBot

NinjaTrader 8 Order Flow strategy.

## Inputs / Parameters

| Name | Type | Property |
|---|---|---|
| Daily Profit Enabled | `bool` | `DailyProfitEnabled` |
| Daily Profit | `double` | `DailyProfit` |
| Daily Loss Enabled | `bool` | `DailyLossEnabled` |
| Daily Loss | `double` | `DailyLoss` |
| Time Enabled | `bool` | `TimeEnabled` |
| Time Start | `string` | `TimeStart` |
| Time End | `string` | `TimeEnd` |
| Ticks Per Level * | `int` | `TicksPerLevel` |
| Imbalance Ratio | `double` | `ImbalanceRatio` |
| Stacked Imbalance | `int` | `StackedImbalance` |
| Imbalance Min Delta | `long` | `ImbalanceMinDelta` |
| Value Area Percentage | `double` | `ValueAreaPercentage` |
| Backtest Enabled | `bool` | `BacktestEnabled` |
| Backtest Strategy Name | `string` | `BacktestStrategyName` |
| Quantity | `int` | `Quantity` |
| Target | `int` | `Target` |
| Stop | `int` | `Stop` |
| External Analysis Service Enabled | `bool` | `ExternalAnalysisServiceEnabled` |
| External Analysis Service HTTP | `string` | `ExternalAnalysisService` |
| Training Data Enabled | `bool` | `TrainingDataEnabled` |
| Training Data Directory | `string` | `TrainingDataDirectory` |

## Installation

1. Copy source files to:  
   `Documents\NinjaTrader 8\bin\Custom\AddOns\OrderFlowBot\`
2. In NinjaTrader 8: **New → NinjaScript Editor → compile**.
3. Add strategy to a chart and configure the inputs above.

## Notes

- Daily profit/loss gates and trading **Time window**.
- Order-flow parameters (ticks per level, imbalance ratio, stacked imbalance, min delta, value area %).
- Optional **External Analysis** HTTP endpoint (enable/disable).
- Optional **Training Data** logging and directory selection.

## Roadmap

- Add TP/SL/BE **sound alerts** (play on TP hit, SL hit, BE move).
- Make **FMS** logic more flexible (k-of-n, thresholds) and expose key tunables.
- Better logs & sample screenshots for entries/exits.
- Unit tests for utility methods (where practical).
