"""Use Florence-2 on a small shortlist; never run across the whole library."""

import argparse
import json
from pathlib import Path
from PIL import Image, ImageEnhance, ImageStat


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("images", type=Path, nargs="+")
    args = parser.parse_args()
    import torch
    from transformers import AutoProcessor, Florence2ForConditionalGeneration

    model_id = "florence-community/Florence-2-base"
    device = "cuda" if torch.cuda.is_available() else "cpu"
    model = Florence2ForConditionalGeneration.from_pretrained(model_id).to(device).eval()
    processor = AutoProcessor.from_pretrained(model_id)
    for path in args.images:
        image = Image.open(path).convert("RGB")
        brightness = ImageStat.Stat(image.convert("L")).mean[0]
        if brightness < 75:
            image = ImageEnhance.Brightness(image).enhance(min(2.6, 105 / max(20, brightness)))
        result = {"image": str(path), "brightness": round(brightness)}
        for task in ("<MORE_DETAILED_CAPTION>", "<OD>"):
            inputs = processor(text=task, images=image, return_tensors="pt")
            inputs = {key: value.to(device) for key, value in inputs.items()}
            with torch.inference_mode():
                ids = model.generate(**inputs, max_new_tokens=128, num_beams=3)
            response = processor.batch_decode(ids, skip_special_tokens=False)[0]
            result[task] = processor.post_process_generation(response, task=task, image_size=image.size)
        print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
