# OrderFlowBot ML Integration Scripts

This directory contains Python scripts for training and serving ML models for the OrderFlowBot FMS strategy.

## Files

- `train_ofb.py` - Training script that converts JSONL data to ML models
- `app.py` - FastAPI service for real-time ML scoring
- `requirements.txt` - Python dependencies
- `README.md` - This file

## Setup

1. Install Python dependencies:
   ```bash
   pip install -r requirements.txt
   ```

2. Ensure you have training data in JSONL format from the FMS strategy

## Training a Model

Train a model from JSONL training data:

```bash
python train_ofb.py --data-dir "C:/temp/" --output-dir "C:/ofb_models/"
```

This will:
- Load all `.jsonl` files from the data directory
- Extract features and labels
- Train a GradientBoostingRegressor
- Save the model as both `.pkl` (for FastAPI) and `.onnx` (for C#)

## Running the ML Service

Start the FastAPI service:

```bash
python app.py --model-path "C:/ofb_models/ofb_model.pkl" --port 5000
```

The service provides these endpoints:
- `GET /health` - Health check
- `POST /analyze` - ML analysis (expects 8 features, returns score and risk)
- `GET /model-info` - Model information

## Integration with FMS Strategy

The FMS strategy will automatically:
1. ~~Try to load the ONNX model from `C:/ofb_models/ofb_model.onnx`~~ **DISABLED in NinjaTrader 8**
2. Use the FastAPI service at `http://localhost:5000/analyze` for ML scoring
3. Blend ML scores with rule-based scores for final decisions
4. Fall back to rule-only scoring if ML service is unavailable

**Note:** ONNX runtime is not available in NinjaTrader 8 due to assembly limitations. The strategy uses the FastAPI service as the primary ML integration method.

## Data Format

The training data should be in JSONL format with this structure:

```json
{
  "ts": "14:30:15",
  "regime": "Transition",
  "stress": 0.62,
  "alphas": {"A": 0.58, "B": 0.41, "C": 0.12},
  "weights": {"A": 0.52, "B": 0.32, "C": 0.16},
  "score": 0.47,
  "side": "LONG",
  "features": {
    "ret1m": 0.001,
    "rvZ": 0.62,
    "ar1": 0.45,
    "cvdSlope": 0.23,
    "entropy": 0.78,
    "hurst": 0.52,
    "imb": 0.34,
    "stress": 0.62
  },
  "proposal": {
    "stopTicks": 12,
    "targetTicks": 18,
    "rrMin": 1.5,
    "qtyEff": 2
  },
  "labels": {
    "fwdRet_5": 0.002,
    "fwdRet_10": 0.003
  }
}
```

## Performance Notes

- The FastAPI service is designed for 100ms response times
- ONNX inference is typically faster than HTTP calls
- Both methods gracefully degrade if unavailable
- The strategy continues to work with rule-based scoring only
