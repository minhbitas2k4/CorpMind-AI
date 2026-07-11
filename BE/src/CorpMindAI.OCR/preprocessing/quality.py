import cv2
import numpy as np


class QualityAnalyzer:

    @staticmethod
    def blur_score(image):
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        return cv2.Laplacian(gray, cv2.CV_64F).var()

    @staticmethod
    def brightness_score(image):
        hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
        return hsv[:, :, 2].mean()

    @staticmethod
    def contrast_score(image):
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        return gray.std()

    @staticmethod
    def noise_score(image):
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)

        blur = cv2.GaussianBlur(gray, (3,3),0)

        noise = cv2.absdiff(gray, blur)

        return noise.mean()

    @staticmethod
    def resolution_score(image):

        h, w = image.shape[:2]

        return w * h

    @staticmethod
    def calculate(image):

        blur = QualityAnalyzer.blur_score(image)

        brightness = QualityAnalyzer.brightness_score(image)

        contrast = QualityAnalyzer.contrast_score(image)

        noise = QualityAnalyzer.noise_score(image)

        resolution = QualityAnalyzer.resolution_score(image)

        score = 100

        if blur < 80:
            score -= 30

        if brightness < 60 or brightness > 220:
            score -= 20

        if contrast < 30:
            score -= 15

        if noise > 20:
            score -= 15

        if resolution < 1000000:
            score -= 20

        score = max(score,0)

        return {
            "score": score,
            "blur": blur,
            "brightness": brightness,
            "contrast": contrast,
            "noise": noise,
            "resolution": resolution
        }