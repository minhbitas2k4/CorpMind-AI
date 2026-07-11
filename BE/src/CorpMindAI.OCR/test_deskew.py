from preprocessing.deskew import deskew_image

angle = deskew_image(
    "output/page_0.png",
    "output/page_0_deskew.png"
)

print(angle)