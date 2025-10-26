#!/usr/bin/env python3
"""
OrderFlowBot FastAPI Service

Provides ML scoring endpoint for FMS strategy integration.
Loads trained model and returns predictions with 100ms timeout.

Requirements:
    pip install fastapi uvicorn numpy pandas scikit-learn

Usage:
    python app.py --model-path "C:/ofb_models/ofb_model.pkl" --port 5000
"""

import argparse
import time
from pathlib import Path
from typing import List

import numpy as np
import joblib
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import uvicorn


class AnalysisRequest(BaseModel):
    features: List[float]


class AnalysisResponse(BaseModel):
    score: float
    risk: float


class OrderFlowBotService:
    def __init__(self, model_path: str):
        self.model_path = Path(model_path)
        self.model = None
        self.load_model()
    
    def load_model(self):
        """Load the trained model."""
        if not self.model_path.exists():
            raise FileNotFoundError(f"Model file not found: {self.model_path}")
        
        try:
            self.model = joblib.load(self.model_path)
            print(f"Loaded model from {self.model_path}")
        except Exception as e:
            raise RuntimeError(f"Failed to load model: {e}")
    
    def predict(self, features: List[float]) -> tuple[float, float]:
        """Make prediction and return (score, risk)."""
        if self.model is None:
            raise RuntimeError("Model not loaded")
        
        if len(features) != 8:
            raise ValueError(f"Expected 8 features, got {len(features)}")
        
        # Convert to numpy array and reshape for prediction
        X = np.array(features).reshape(1, -1)
        
        # Make prediction
        prediction = self.model.predict(X)[0]
        
        # Convert prediction to score [0,1]
        score = max(0.0, min(1.0, prediction))
        
        # Calculate risk as inverse of score (simplified)
        risk = max(0.0, min(1.0, 1.0 - score))
        
        return score, risk


# Global service instance
service = None


def create_app(model_path: str) -> FastAPI:
    """Create FastAPI application."""
    global service
    
    app = FastAPI(
        title="OrderFlowBot ML Service",
        description="ML scoring service for FMS strategy",
        version="1.0.0"
    )
    
    # Initialize service
    service = OrderFlowBotService(model_path)
    
    @app.get("/health")
    async def health_check():
        """Health check endpoint."""
        return {"status": "healthy", "model_loaded": service.model is not None}
    
    @app.post("/analyze", response_model=AnalysisResponse)
    async def analyze(request: AnalysisRequest):
        """Analyze features and return ML score and risk assessment."""
        start_time = time.time()
        
        try:
            score, risk = service.predict(request.features)
            
            # Check timeout (should be handled by FastAPI timeout, but just in case)
            elapsed = time.time() - start_time
            if elapsed > 0.1:  # 100ms timeout
                print(f"Warning: Analysis took {elapsed*1000:.1f}ms")
            
            return AnalysisResponse(score=score, risk=risk)
            
        except ValueError as e:
            raise HTTPException(status_code=400, detail=str(e))
        except Exception as e:
            raise HTTPException(status_code=500, detail=f"Analysis failed: {str(e)}")
    
    @app.get("/model-info")
    async def model_info():
        """Get model information."""
        if service.model is None:
            raise HTTPException(status_code=500, detail="Model not loaded")
        
        return {
            "model_type": type(service.model).__name__,
            "model_path": str(service.model_path),
            "n_features": getattr(service.model, 'n_features_', 'unknown')
        }
    
    return app


def main():
    parser = argparse.ArgumentParser(description="OrderFlowBot FastAPI Service")
    parser.add_argument("--model-path", required=True, help="Path to trained model (.pkl)")
    parser.add_argument("--port", type=int, default=5000, help="Port to run service on")
    parser.add_argument("--host", default="127.0.0.1", help="Host to bind to")
    
    args = parser.parse_args()
    
    try:
        app = create_app(args.model_path)
        
        print(f"Starting OrderFlowBot ML Service on {args.host}:{args.port}")
        print(f"Model: {args.model_path}")
        print("Endpoints:")
        print(f"  GET  /health     - Health check")
        print(f"  POST /analyze    - ML analysis")
        print(f"  GET  /model-info - Model information")
        
        uvicorn.run(
            app,
            host=args.host,
            port=args.port,
            timeout_keep_alive=30,
            timeout_graceful_shutdown=5
        )
        
    except Exception as e:
        print(f"Error starting service: {e}")
        return 1
    
    return 0


if __name__ == "__main__":
    exit(main())

