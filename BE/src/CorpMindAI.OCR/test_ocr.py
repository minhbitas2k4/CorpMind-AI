from ocr.paddle_engine import PaddleEngine
from ocr.pipeline import OCRPipeline
from validators.confidence import evaluate, evaluate_components
from validators.rules import validate
from ocr.parser import parse_structure_result

pipeline = OCRPipeline()

response = pipeline.process_image(
    "output/page_0.png"
)

print("Overall:", evaluate(response), "-", response.page_average_confidence)
print("=" * 60)

for item in evaluate_components(response):
    print(f"[{item['level'].upper():12}] ({item['average_confidence']:.4f}) {item['raw_text']}")

# ---- Thêm phần validate rule ----
print("=" * 60)
errors = validate(response)

if errors:
    print(f"⚠️  Có {len(errors)} dòng cần kiểm tra lại:")
    for err in errors:
        print(f"  - [{err['component_type']}] '{err['text']}' (confidence: {err['confidence']:.2f})")
else:
    print("✅ Không có dòng nào dưới ngưỡng tin cậy.")