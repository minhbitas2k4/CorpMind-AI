import os
import fitz
import cv2

def convert_pdf_to_images(pdf_path, output_dir="output", dpi=200):

    if not os.path.exists(output_dir):
        os.makedirs(output_dir)

    image_paths=[]
    with fitz.open(pdf_path) as doc:
        for i,page in enumerate(doc):
            pix = page.get_pixmap(dpi=dpi)
            filename = os.path.join(output_dir, f"page_{i}.png")
            pix.save(filename)
            image_paths.append(filename)

    return image_paths
