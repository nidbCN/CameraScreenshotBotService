from fastapi import FastAPI, UploadFile, File
from fastapi.responses import JSONResponse
from ultralytics import YOLO
import cv2
import numpy as np

app = FastAPI()
model = YOLO('best.pt')  # 加载模型

@app.post("/faces")
async def predict(file: UploadFile = File(...)):
    # 读取文件
    contents = await file.read()
    np_arr = np.frombuffer(contents, np.uint8)
    image = cv2.imdecode(np_arr, cv2.IMREAD_COLOR)

    # 推理
    results = model(image)
    detections = results[0].boxes.xyxy.numpy()  # 获取边界框
    confidences = results[0].boxes.conf.numpy()  # 获取置信度

    # 过滤结果
    filtered_results = []
    for box, conf in zip(detections, confidences):
        if conf >= 0.7:
            x1, y1, x2, y2 = box
            filtered_results.append({
                "x": int(x1),
                "y": int(y1),
                "width": int(x2 - x1),
                "height": int(y2 - y1)
            })

    return JSONResponse(content=filtered_results)

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=9331)
