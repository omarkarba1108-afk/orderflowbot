#!/usr/bin/env python3
"""
OrderFlowBot ML Training Script

Trains a GradientBoostingRegressor on JSONL training data from FMS strategy.
Exports both .pkl for FastAPI service and .onnx for C# ONNX runtime.

Requirements:
    pip install numpy pandas scikit-learn skl2onnx onnxruntime

Usage:
    python train_ofb.py --data-dir "C:/temp/" --output-dir "C:/ofb_models/"
"""

import argparse
import json
import os
import sys
from pathlib import Path
from typing import List, Dict, Any

import numpy as np
import pandas as pd
from sklearn.ensemble import GradientBoostingRegressor
from sklearn.model_selection import train_test_split
from sklearn.metrics import mean_squared_error, r2_score
import joblib
from skl2onnx import convert_sklearn
from skl2onnx.common.data_types import FloatTensorType


def load_jsonl_data(data_dir: str) -> pd.DataFrame:
    """Load JSONL training data from directory."""
    data_files = list(Path(data_dir).glob("*.jsonl"))
    if not data_files:
        raise FileNotFoundError(f"No .jsonl files found in {data_dir}")
    
    print(f"Found {len(data_files)} JSONL files")
    
    all_data = []
    for file_path in data_files:
        print(f"Loading {file_path.name}...")
        with open(file_path, 'r') as f:
            for line_num, line in enumerate(f, 1):
                try:
                    data = json.loads(line.strip())
                    all_data.append(data)
                except json.JSONDecodeError as e:
                    print(f"Warning: Skipping invalid JSON at line {line_num} in {file_path.name}: {e}")
                    continue
    
    if not all_data:
        raise ValueError("No valid data found in JSONL files")
    
    print(f"Loaded {len(all_data)} training samples")
    return pd.DataFrame(all_data)


def extract_features_and_labels(df: pd.DataFrame) -> tuple:
    """Extract features and labels from DataFrame."""
    # Extract features from nested structure
    feature_cols = ['ret1m', 'rvZ', 'ar1', 'cvdSlope', 'entropy', 'hurst', 'imb', 'stress']
    
    # Flatten features if they're nested
    if 'features' in df.columns:
        features_df = pd.json_normalize(df['features'])
        features = features_df[feature_cols].values
    else:
        # Features are already flattened
        features = df[feature_cols].values
    
    # Use forward return as label (target variable)
    if 'labels' in df.columns:
        labels_df = pd.json_normalize(df['labels'])
        labels = labels_df['fwdRet_10'].values  # 10-minute forward return
    else:
        # Labels are already flattened
        labels = df['fwdRet_10'].values
    
    # Remove any NaN values
    valid_mask = ~(np.isnan(features).any(axis=1) | np.isnan(labels))
    features = features[valid_mask]
    labels = labels[valid_mask]
    
    print(f"Extracted {len(features)} valid samples with {features.shape[1]} features")
    return features, labels


def train_model(features: np.ndarray, labels: np.ndarray) -> GradientBoostingRegressor:
    """Train GradientBoostingRegressor model."""
    print("Training GradientBoostingRegressor...")
    
    # Split data
    X_train, X_test, y_train, y_test = train_test_split(
        features, labels, test_size=0.2, random_state=42
    )
    
    # Train model
    model = GradientBoostingRegressor(
        n_estimators=100,
        learning_rate=0.1,
        max_depth=6,
        random_state=42
    )
    
    model.fit(X_train, y_train)
    
    # Evaluate
    y_pred = model.predict(X_test)
    mse = mean_squared_error(y_test, y_pred)
    r2 = r2_score(y_test, y_pred)
    
    print(f"Model Performance:")
    print(f"  MSE: {mse:.6f}")
    print(f"  R²:  {r2:.6f}")
    
    return model


def save_model_and_onnx(model: GradientBoostingRegressor, output_dir: str):
    """Save model as .pkl and convert to .onnx."""
    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)
    
    # Save as .pkl for FastAPI service
    pkl_path = output_path / "ofb_model.pkl"
    joblib.dump(model, pkl_path)
    print(f"Saved model to {pkl_path}")
    
    # Convert to ONNX for C# runtime
    onnx_path = output_path / "ofb_model.onnx"
    
    # Define input type (8 features)
    initial_type = [('input', FloatTensorType([None, 8]))]
    
    try:
        onnx_model = convert_sklearn(
            model,
            initial_types=initial_type,
            target_opset=11
        )
        
        with open(onnx_path, "wb") as f:
            f.write(onnx_model.SerializeToString())
        
        print(f"Saved ONNX model to {onnx_path}")
        
    except Exception as e:
        print(f"Warning: Failed to convert to ONNX: {e}")
        print("ONNX model will not be available for C# runtime")


def main():
    parser = argparse.ArgumentParser(description="Train OrderFlowBot ML model")
    parser.add_argument("--data-dir", required=True, help="Directory containing JSONL training data")
    parser.add_argument("--output-dir", required=True, help="Output directory for models")
    parser.add_argument("--min-samples", type=int, default=1000, help="Minimum samples required")
    
    args = parser.parse_args()
    
    try:
        # Load data
        df = load_jsonl_data(args.data_dir)
        
        if len(df) < args.min_samples:
            print(f"Warning: Only {len(df)} samples found, minimum recommended is {args.min_samples}")
        
        # Extract features and labels
        features, labels = extract_features_and_labels(df)
        
        if len(features) < 100:
            print("Error: Insufficient valid training samples")
            sys.exit(1)
        
        # Train model
        model = train_model(features, labels)
        
        # Save models
        save_model_and_onnx(model, args.output_dir)
        
        print("Training completed successfully!")
        
    except Exception as e:
        print(f"Error: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()

