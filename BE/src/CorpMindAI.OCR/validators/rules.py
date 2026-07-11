# # validators/rules.py
# def validate(response):
#     errors = []

#     for component in response.components:
#         for block in component.blocks:
#             if block.confidence < 0.80:
#                 errors.append({
#                     "text": block.text,
#                     "confidence": block.confidence,
#                     "component_type": component.component_type,
#                     "reason": "Low confidence",
#                 })

#     return errors
def validate(response, threshold: float = 0.80):
    """
    Kiểm tra từng dòng chữ (block) trong response, trả về danh sách
    những dòng có confidence THẤP HƠN threshold — tức là những chỗ
    đáng ngờ, cần con người xem lại.
    """
    errors = []

    for component in response.components:
        for block in component.blocks:
            if block.confidence < threshold:
                errors.append({
                    "text": block.text,
                    "confidence": block.confidence,
                    "component_type": component.component_type,
                    "reason": f"Confidence below threshold ({threshold})",
                })

    return errors