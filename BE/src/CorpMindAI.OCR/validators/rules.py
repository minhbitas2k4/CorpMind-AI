def validate(response, threshold: float = 0.80):
    """
    Kiểm tra từng dòng chữ (block) trong response, trả về danh sách
    những dòng có confidence THẤP HƠN threshold.
    """
    errors = []

    for component in response.components:
        for block in component.blocks:
            if block.confidence < threshold:
                errors.append({
                    "text": block.text,
                    "confidence": block.confidence,
                    "component_type": component.component_type.value,  # enum -> string
                    "reason": f"Confidence below threshold ({threshold})",
                })

    return errors