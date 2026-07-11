# ocr/pipeline.py
from ocr.paddle_engine import PaddleEngine


class OCRPipeline:

    def __init__(self):
        self.engine = PaddleEngine()

    def process_image(self, image_path):
        # recognize() đã parse sẵn thành StructureResponse, KHÔNG parse lại lần 2
        response = self.engine.recognize(image_path)
        return response