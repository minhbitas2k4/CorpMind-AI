from preprocessing.pdf_to_image import convert_pdf_to_images

images = convert_pdf_to_images(
    "ocr_test_image_only.pdf",
    "output"
)

print(images)