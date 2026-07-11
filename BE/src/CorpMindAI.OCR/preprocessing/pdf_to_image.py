import os
import fitz
import cv2

def convert_pdf_to_images(pdf_path, output_dir="output"):

    if not os.path.exists(output_dir):
        os.makedirs(output_dir)

    doc = fitz.open(pdf_path)

    image_paths=[]

    for i,page in enumerate(doc):

        pix = page.get_pixmap(dpi=300)

        filename = os.path.join(output_dir, f"page_{i}.png")

        pix.save(filename)

        image_paths.append(filename)

    return image_paths