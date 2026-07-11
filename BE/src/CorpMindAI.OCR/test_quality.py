from preprocessing.pdf_to_image import convert_pdf_to_images
from preprocessing.quality import QualityAnalyzer
import cv2

image_paths = convert_pdf_to_images("ocr_test_image_only.pdf")

for path in image_paths:
    image = cv2.imread(path)
    if image is None:
        print(f"Không đọc được ảnh: {path}")
        continue
    result = QualityAnalyzer.calculate(image)
    print(result)