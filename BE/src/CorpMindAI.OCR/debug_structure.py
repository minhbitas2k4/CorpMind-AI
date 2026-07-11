import json
from ocr.paddle_engine import PaddleEngine

engine = PaddleEngine(lang="vi", table=True, ocr=True)
raw = engine.analyze("output/page_0.png")   # <-- đổi thành đúng file bạn đang có

print(f"Total regions: {len(raw)}\n")
for i, region in enumerate(raw):
    print(f"=== Region {i} ===")
    print(f"  type : {region.get('type')}")
    print(f"  bbox : {region.get('bbox')}")
    res = region.get("res")
    try:
        print(f"  res (json): {json.dumps(res, ensure_ascii=False, default=str)[:800]}")
    except Exception:
        print(f"  res (str):  {str(res)[:800]}")
    print()