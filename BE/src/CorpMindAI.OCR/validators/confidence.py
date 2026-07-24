def get_confidence_level(score: float) -> str:
    """
    Phân loại một điểm confidence (0.0 - 1.0) thành mức độ tin cậy.
    """
    if score >= 0.95:
        return "excellent"
    if score >= 0.90:
        return "good"
    if score >= 0.80:
        return "warning"
    return "need_review"


def evaluate(response):
    """
    Đánh giá độ tin cậy tổng thể của toàn trang, dựa trên
    page_average_confidence.
    """
    return get_confidence_level(response.page_average_confidence)


def evaluate_components(response):
    """
    Đánh giá độ tin cậy CHI TIẾT theo từng component (title, text, table...),
    trả về danh sách dict để dễ serialize thành JSON qua API.
    """
    results = []
    for component in response.components:
        results.append({
            "component_type": component.component_type.value,  # enum -> string
            "average_confidence": component.average_confidence,
            "level": get_confidence_level(component.average_confidence),
            "raw_text": component.raw_text,
            "bbox": component.bbox,
        })
    return results