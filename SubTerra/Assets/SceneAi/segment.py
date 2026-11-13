"""Local, bounded ScanNet-20 inference. Input rows are little-endian XYZ RGB float32."""
import base64
import json
import os
import sys
from pathlib import Path

import numpy as np
import torch
import point_transformer_v3 as ptv3
from scipy.spatial import cKDTree


def partitions(coords, indices, maximum=8000):
    if len(indices) <= maximum:
        yield indices
        return
    axis = np.ptp(coords[indices], axis=0).argmax()
    ordered = indices[np.argsort(coords[indices, axis], kind="stable")]
    middle = len(ordered) // 2
    yield from partitions(coords, ordered[:middle], maximum)
    yield from partitions(coords, ordered[middle:], maximum)


def normals_for(coords):
    tree = cKDTree(coords)
    _, neighbors = tree.query(coords, k=min(16, len(coords)), workers=2)
    offsets = coords[neighbors] - coords[neighbors].mean(axis=1, keepdims=True)
    covariance = np.einsum("nki,nkj->nij", offsets, offsets)
    _, vectors = np.linalg.eigh(covariance)
    normals = vectors[:, :, 0]
    toward_center = np.median(coords, axis=0) - coords
    normals[(normals * toward_center).sum(axis=1) < 0] *= -1
    return np.ascontiguousarray(normals, dtype=np.float32)


def main():
    torch.set_num_threads(min(4, os.cpu_count() or 1))
    torch.manual_seed(42)
    np.random.seed(42)
    # The torch backends work on Windows without a separate native compiler.
    ptv3.set_backend(attention="sdpa", conv="torch", layer_norm="torch")
    model = ptv3.load("ptv3_scannet", warmup=False)
    if len(sys.argv) == 3 and sys.argv[1] == "--install":
        manifest = {"model": "ptv3_scannet", "package": ptv3.__version__,
                    "torch": torch.__version__, "device": str(next(model.parameters()).device),
                    "weights": model.weights_info}
        target = Path(sys.argv[2])
        temporary = target.with_suffix(".tmp")
        temporary.write_text(json.dumps(manifest, default=str, indent=2), encoding="utf-8")
        temporary.replace(target)
        return
    if len(sys.argv) != 3:
        raise ValueError("Expected input.bin output.json")
    raw = np.fromfile(sys.argv[1], dtype="<f4")
    if raw.size % 6 or not 64 <= raw.size // 6 <= 120000 or not np.isfinite(raw).all():
        raise ValueError("Invalid scene input")
    rows = raw.reshape(-1, 6)
    coords = np.ascontiguousarray(rows[:, :3])
    color = np.ascontiguousarray(rows[:, 3:])
    if (color < 0).any() or (color > 255).any():
        raise ValueError("RGB outside 0..255")
    normals = normals_for(coords)
    chunks = list(partitions(coords, np.arange(len(coords))))
    labels = np.zeros(len(coords), dtype=np.uint8)
    scores = np.zeros(len(coords), dtype=np.float32)
    print("PROGRESS 15", flush=True)
    with torch.inference_mode():
        for number, core in enumerate(chunks):
            low = coords[core].min(axis=0) - 0.4
            high = coords[core].max(axis=0) + 0.4
            context = np.flatnonzero(((coords >= low) & (coords <= high)).all(axis=1))
            extra = np.setdiff1d(context, core, assume_unique=True)
            if len(extra) > 4000:
                distance = ((coords[extra] - coords[core].mean(axis=0)) ** 2).sum(axis=1)
                extra = extra[np.argsort(distance, kind="stable")[:4000]]
            selected = np.concatenate((core, extra))
            logits = model.predict(coords[selected], color[selected], normals[selected])
            if logits.shape != (len(selected), 20) or not torch.isfinite(logits).all():
                raise ValueError("Invalid model output")
            confidence, classes = logits.float().softmax(dim=-1).max(dim=-1)
            confidence = confidence[:len(core)].cpu().numpy()
            classes = classes[:len(core)].cpu().numpy().astype(np.uint8) + 1
            classes[confidence < 0.55] = 0
            labels[core] = classes
            scores[core] = confidence
            del logits
            print(f"PROGRESS {15 + 80 * (number + 1) // len(chunks)}", flush=True)
    result = {"Version": 1, "Model": "ptv3_scannet",
              "Labels": base64.b64encode(labels.tobytes()).decode("ascii"),
              "Scores": np.round(scores, 6).tolist()}
    target = Path(sys.argv[2])
    temporary = target.with_suffix(".tmp")
    temporary.write_text(json.dumps(result, allow_nan=False), encoding="utf-8")
    temporary.replace(target)


if __name__ == "__main__":
    main()
