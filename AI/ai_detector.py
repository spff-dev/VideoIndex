"""
Local AI Content Detector for VideoIndex
Detects performer count and OnlyFans watermarks
100% local processing - no data leaves your machine
"""

import cv2
import sys
import json
import re
from pathlib import Path
from typing import Optional, Tuple, List, Dict
import numpy as np

# Check if required packages are installed
try:
    from ultralytics import YOLO
    import easyocr
except ImportError:
    print(json.dumps({
        "error": "Missing required packages",
        "message": "Please run: pip install ultralytics opencv-python easyocr torch torchvision",
        "performer_count": None,
        "onlyfans_detected": False,
        "onlyfans_username": None,
        "confidence": 0.0
    }))
    sys.exit(1)


class LocalAIDetector:
    """Local AI detector for NSFW content analysis"""
    
    def __init__(self, model_path: str = "yolov8n.pt"):
        """Initialize detector with local models"""
        self.model_path = model_path
        self.person_detector = None
        self.ocr_reader = None
        self._load_models()
    
    def _load_models(self):
        """Load AI models (cached after first run)"""
        try:
            # Load YOLOv8 for person detection (downloads automatically on first run)
            self.person_detector = YOLO(self.model_path)
            
            # Load EasyOCR for text detection (downloads automatically on first run)
            # gpu=False ensures CPU-only operation for privacy
            self.ocr_reader = easyocr.Reader(['en'], gpu=False, verbose=False)
            
        except Exception as e:
            print(json.dumps({
                "error": f"Failed to load models: {str(e)}",
                "performer_count": None,
                "onlyfans_detected": False,
                "onlyfans_username": None,
                "confidence": 0.0
            }), file=sys.stderr)
            sys.exit(1)
    
    def count_performers(self, video_path: str, num_frames: int = 8) -> Optional[int]:
        """
        Count people across multiple video frames
        
        Args:
            video_path: Path to video file
            num_frames: Number of frames to sample
            
        Returns:
            Most common person count, or None if detection fails
        """
        try:
            cap = cv2.VideoCapture(video_path)
            if not cap.isOpened():
                return None
            
            total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
            if total_frames == 0:
                return None
            
            # Sample frames evenly throughout video
            frame_indices = [
                int(total_frames * i / (num_frames + 1)) 
                for i in range(1, num_frames + 1)
            ]
            
            counts = []
            for idx in frame_indices:
                cap.set(cv2.CAP_PROP_POS_FRAMES, idx)
                ret, frame = cap.read()
                if not ret:
                    continue
                
                # Detect people in frame (class 0 = person in COCO dataset)
                results = self.person_detector(frame, classes=[0], verbose=False)
                
                # Count detected persons
                if len(results) > 0 and hasattr(results[0], 'boxes'):
                    person_count = len(results[0].boxes)
                    counts.append(person_count)
            
            cap.release()
            
            if not counts:
                return None
            
            # Return most common count (mode)
            return max(set(counts), key=counts.count)
            
        except Exception as e:
            print(f"Error counting performers: {e}", file=sys.stderr)
            return None
    
    def detect_watermark(self, video_path: str) -> Tuple[Optional[str], bool, float]:
        """
        Detect OnlyFans watermarks and extract username
        
        Args:
            video_path: Path to video file
            
        Returns:
            Tuple of (username, has_onlyfans, confidence)
        """
        try:
            cap = cv2.VideoCapture(video_path)
            if not cap.isOpened():
                return None, False, 0.0
            
            total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
            if total_frames == 0:
                return None, False, 0.0
            
            # Sample frames (especially beginning, middle, and end)
            sample_positions = [0.05, 0.25, 0.50, 0.75, 0.95]
            frame_indices = [int(total_frames * pos) for pos in sample_positions]
            
            detections = []
            
            for idx in frame_indices:
                cap.set(cv2.CAP_PROP_POS_FRAMES, idx)
                ret, frame = cap.read()
                if not ret:
                    continue
                
                # Extract corner regions where watermarks typically appear
                corners = self._get_corners(frame)
                
                for corner in corners:
                    # Run OCR on corner
                    try:
                        text_results = self.ocr_reader.readtext(
                            corner, 
                            detail=0,
                            paragraph=False
                        )
                        
                        # Combine detected text
                        text = ' '.join(text_results).lower()
                        
                        # Check for OnlyFans mentions
                        if self._has_onlyfans_text(text):
                            username = self._extract_username(text)
                            if username:
                                detections.append(username)
                                
                    except Exception as e:
                        continue
            
            cap.release()
            
            if not detections:
                return None, False, 0.0
            
            # Find most common username
            most_common = max(set(detections), key=detections.count)
            confidence = detections.count(most_common) / len(detections)
            
            # High confidence threshold
            has_onlyfans = confidence > 0.4
            
            return most_common if has_onlyfans else None, has_onlyfans, confidence
            
        except Exception as e:
            print(f"Error detecting watermark: {e}", file=sys.stderr)
            return None, False, 0.0
    
    def _get_corners(self, frame: np.ndarray) -> List[np.ndarray]:
        """Extract corner regions where watermarks typically appear"""
        h, w = frame.shape[:2]
        
        # Corner sizes (watermarks are usually in corners)
        corner_w = w // 4
        corner_h = h // 6
        
        corners = [
            frame[0:corner_h, 0:corner_w],                    # Top-left
            frame[0:corner_h, w-corner_w:w],                  # Top-right
            frame[h-corner_h:h, 0:corner_w],                  # Bottom-left
            frame[h-corner_h:h, w-corner_w:w],                # Bottom-right
        ]
        
        return corners
    
    def _has_onlyfans_text(self, text: str) -> bool:
        """Check if text contains OnlyFans references"""
        patterns = [
            r'onlyfans',
            r'only\s*fans',
            r'of\s*:',
            r'of\s*/',
            r'@.*onlyfans',
        ]
        
        for pattern in patterns:
            if re.search(pattern, text, re.IGNORECASE):
                return True
        return False
    
    def _extract_username(self, text: str) -> Optional[str]:
        """Extract username from watermark text"""
        # Common OnlyFans watermark patterns
        patterns = [
            r'onlyfans\.com/([a-zA-Z0-9_-]+)',
            r'onlyfans\.com\\([a-zA-Z0-9_-]+)',
            r'onlyfans:\s*([a-zA-Z0-9_-]+)',
            r'of:\s*([a-zA-Z0-9_-]+)',
            r'of/([a-zA-Z0-9_-]+)',
            r'@([a-zA-Z0-9_-]+)',
            r'/([a-zA-Z0-9_-]+)',
        ]
        
        for pattern in patterns:
            match = re.search(pattern, text, re.IGNORECASE)
            if match:
                username = match.group(1)
                # Validate username (3-30 chars, alphanumeric + underscore/dash)
                if 3 <= len(username) <= 30 and re.match(r'^[a-zA-Z0-9_-]+$', username):
                    return username.lower()
        
        return None
    
    def analyze_video(self, video_path: str) -> Dict:
        """
        Main analysis function - analyzes video and returns results
        
        Args:
            video_path: Path to video file
            
        Returns:
            Dictionary with analysis results
        """
        result = {
            'success': True,
            'performer_count': None,
            'onlyfans_detected': False,
            'onlyfans_username': None,
            'confidence': 0.0,
            'error': None
        }
        
        try:
            # Verify video file exists
            if not Path(video_path).exists():
                result['success'] = False
                result['error'] = 'Video file not found'
                return result
            
            # Count performers
            count = self.count_performers(video_path)
            if count is not None:
                result['performer_count'] = count
            
            # Detect watermarks
            username, has_onlyfans, confidence = self.detect_watermark(video_path)
            if has_onlyfans:
                result['onlyfans_detected'] = True
                result['onlyfans_username'] = username
                result['confidence'] = confidence
            
            return result
            
        except Exception as e:
            result['success'] = False
            result['error'] = str(e)
            return result


def main():
    """CLI entry point"""
    if len(sys.argv) < 2:
        print(json.dumps({
            'error': 'Usage: python ai_detector.py <video_path>',
            'success': False
        }))
        sys.exit(1)
    
    video_path = sys.argv[1]
    
    # Initialize detector
    detector = LocalAIDetector()
    
    # Analyze video
    result = detector.analyze_video(video_path)
    
    # Output JSON for C# to consume
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
