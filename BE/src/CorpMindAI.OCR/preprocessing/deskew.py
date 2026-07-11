import cv2
import numpy as np
from deskew import determine_skew


def deskew_image(input_path: str, output_path: str):
    """
    Làm thẳng một ảnh bị nghiêng.
    """

    image = cv2.imread(input_path)

    if image is None:
        raise Exception(f"Cannot read image: {input_path}")

    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)

    angle = determine_skew(gray)

    if angle is None:
        angle = 0

    h, w = image.shape[:2]

    center = (w // 2, h // 2)

    matrix = cv2.getRotationMatrix2D(center, angle, 1.0)

    rotated = cv2.warpAffine(
        image,
        matrix,
        (w, h),
        flags=cv2.INTER_CUBIC,
        borderMode=cv2.BORDER_REPLICATE
    )

    cv2.imwrite(output_path, rotated)

    return angle