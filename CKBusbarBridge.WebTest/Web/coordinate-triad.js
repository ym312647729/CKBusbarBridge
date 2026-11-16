import { Vector3 } from './lib/three/three.module.js';

// Screen-space view triad: rotates with the current camera, independent of model zoom/pan.
export function createCoordinateTriad(parent) {
    const triadCanvas = document.createElement('canvas'); triadCanvas.className = 'coordinate-triad';
    triadCanvas.setAttribute('aria-label', '视图坐标系'); triadCanvas.title = '视图坐标系'; parent.append(triadCanvas);
    const drawingContext = triadCanvas.getContext('2d'), projectedDirection = new Vector3();
    const axisDefinitions = [{ name: 'X', direction: [1, 0, 0], color: '#cc3939' }, { name: 'Y', direction: [0, 1, 0], color: '#308b42' }, { name: 'Z', direction: [0, 0, 1], color: '#3569c4' }];
    return {
        update(camera) {
            const ratio = Math.min(window.devicePixelRatio || 1, 2), size = 112;
            if (triadCanvas.width !== size * ratio) { triadCanvas.width = size * ratio; triadCanvas.height = size * ratio; }
            drawingContext.setTransform(ratio, 0, 0, ratio, 0, 0); drawingContext.clearRect(0, 0, size, size);
            const visibleAxes = axisDefinitions.map(definition => {
                projectedDirection.fromArray(definition.direction).transformDirection(camera.matrixWorldInverse);
                return { ...definition, x: projectedDirection.x, y: -projectedDirection.y, depth: projectedDirection.z };
            }).sort((first, last) => first.depth - last.depth);
            for (const axis of visibleAxes) {
                const origin = [50, 63], end = [origin[0] + axis.x * 37, origin[1] + axis.y * 37], span = Math.hypot(axis.x, axis.y);
                drawingContext.strokeStyle = axis.color; drawingContext.fillStyle = axis.color; drawingContext.lineWidth = 1.6;
                if (span > 0.08) {
                    const alongX = axis.x / span, alongY = axis.y / span;
                    drawingContext.beginPath(); drawingContext.moveTo(...origin); drawingContext.lineTo(...end); drawingContext.stroke();
                    drawingContext.beginPath(); drawingContext.moveTo(...end);
                    drawingContext.lineTo(end[0] - alongX * 6 - alongY * 2.4, end[1] - alongY * 6 + alongX * 2.4);
                    drawingContext.lineTo(end[0] - alongX * 6 + alongY * 2.4, end[1] - alongY * 6 - alongX * 2.4); drawingContext.closePath(); drawingContext.fill();
                    drawingContext.font = '13px "Microsoft YaHei"'; drawingContext.textAlign = 'center'; drawingContext.textBaseline = 'middle';
                    drawingContext.fillText(axis.name, end[0] + alongX * 10, end[1] + alongY * 10);
                } else { drawingContext.beginPath(); drawingContext.arc(...origin, 2.5, 0, Math.PI * 2); drawingContext.fill(); }
            }
            drawingContext.fillStyle = '#f8fafc'; drawingContext.strokeStyle = '#6f7c8e'; drawingContext.lineWidth = 1;
            drawingContext.fillRect(47, 60, 6, 6); drawingContext.strokeRect(47, 60, 6, 6);
        },
        dispose() { triadCanvas.remove(); }
    };
}
