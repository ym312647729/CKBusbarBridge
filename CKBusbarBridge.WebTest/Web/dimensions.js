import { Vector3 } from './lib/three/three.module.js';
import { formatParameter } from './parameter-numbers.js';

// Projected dimensions and their edit targets share the renderer's camera frame.
export function createDimensionOverlay(container, onSelected, onEdit = () => {}) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.classList.add('dimension-lines'); svg.setAttribute('aria-hidden', 'true'); container.append(svg);
    const projectedPoint = new Vector3(); let labels = [];
    const project = (point, camera, width, height) => {
        projectedPoint.fromArray(point).project(camera);
        return { x: (projectedPoint.x + 1) * width / 2, y: (1 - projectedPoint.y) * height / 2, depth: projectedPoint.z };
    };
    const line = (first, last) => `M${first.x},${first.y}L${last.x},${last.y}`;
    function reset(meshes) {
        for (const label of labels) label.node.remove(); svg.replaceChildren();
        labels = meshes.flatMap(definition => definition.dimensionDefinitions || [definition]).map(definition => {
            const node = document.createElement('button'), path = document.createElementNS(svg.namespaceURI, 'path');
            node.className = 'dimension-label' + (definition.dimensionKind ? ' editable' : '') + (definition.defaultParameter ? ' default-parameter' : ''); node.textContent = definition.annotationText || formatParameter(definition.lengthMm);
            const measureName = { length: '排长', width: '排宽', thickness: '排厚', upper: '上端标高', lower: '始端距柜顶', cabinetHeight: '柜体高度' }[definition.dimensionKind] ?? '';
            node.title = `${definition.name} · ${definition.defaultParameter ? '默认值，可修改 · ' : ''}${definition.dimensionKind ? '双击修改' + measureName : '尺寸'}`;
            if (definition.annotationText) node.title = definition.name + ' · ' + definition.annotationText;
            node.addEventListener('pointerdown', event => event.stopPropagation());
            node.addEventListener('click', () => onSelected(definition.id));
            node.addEventListener('dblclick', event => { event.stopPropagation(); onEdit(definition, event.clientX, event.clientY); });
            container.append(node); svg.append(path);
            return { node, path, definition, width: node.offsetWidth, height: node.offsetHeight };
        });
    }
    function update(camera, width, height, visible, excluded = new Set(), selectedId = null) {
        svg.setAttribute('viewBox', `0 0 ${width} ${height}`); svg.style.display = visible ? '' : 'none';
        const pixelsPerMeter = height * camera.zoom / (camera.top - camera.bottom), textClearance = 0.0005 * pixelsPerMeter;
        for (const label of labels) {
            if (label.definition.annotationText) {
                const point=project(label.definition.anchor,camera,width,height);
                label.node.hidden=!visible || label.definition.id!==selectedId || excluded.has(label.definition.id) || Math.abs(point.depth)>1;
                label.path.style.display='none';
                label.node.style.left=`${point.x}px`; label.node.style.top=`${point.y}px`;
                label.node.style.transform='translate(-50%,-50%)';
                continue;
            }
            let chosen = null, closestDepth = Infinity;
            for (const candidate of label.definition.dimensionLines || []) {
                const first = project(candidate.start, camera, width, height), last = project(candidate.end, camera, width, height);
                const depth = (first.depth + last.depth) / 2;
                if (depth < closestDepth) { closestDepth = depth; chosen = { first, last, candidate }; }
            }
            label.node.hidden = !visible || !label.definition.alwaysVisible && label.definition.id !== selectedId || excluded.has(label.definition.id) || !chosen || closestDepth < -1 || closestDepth > 1;
            label.path.style.display = label.node.hidden ? 'none' : '';
            if (label.node.hidden) continue;
            const { first, last, candidate } = chosen, dx = last.x - first.x, dy = last.y - first.y, span = Math.hypot(dx, dy);
            if (span < 1e-6) { label.node.hidden = true; label.path.style.display = 'none'; continue; }
            const along = { x: dx / span, y: dy / span }, outward = { x: -dy / span, y: dx / span };
            const midpoint = { x: (first.x + last.x) / 2, y: (first.y + last.y) / 2 };
            const modelCenter = project(label.definition.anchor, camera, width, height);
            if ((midpoint.x - modelCenter.x) * outward.x + (midpoint.y - modelCenter.y) * outward.y < 0) { outward.x *= -1; outward.y *= -1; }
            const offset = label.definition.dimensionKind === 'upper' ? 44 : 16, shift = (point, amount) => ({ x: point.x + outward.x * amount, y: point.y + outward.y * amount });
            const dimensionStart = shift(first, offset), dimensionEnd = shift(last, offset), dimensionMiddle = shift(midpoint, offset);
            const compact = span < label.width + 12, halfGap = compact ? 0 : Math.min(label.width / 2 + 3, span / 2 - 5);
            const gapStart = { x: dimensionMiddle.x - along.x * halfGap, y: dimensionMiddle.y - along.y * halfGap };
            const gapEnd = { x: dimensionMiddle.x + along.x * halfGap, y: dimensionMiddle.y + along.y * halfGap };
            let pathData = line(project(candidate.startWitness, camera, width, height), shift(first, offset + 4)) + line(project(candidate.endWitness, camera, width, height), shift(last, offset + 4));
            pathData += line(dimensionStart, gapStart) + line(gapEnd, dimensionEnd);
            for (const [tip, sign] of [[dimensionStart, 1], [dimensionEnd, -1]]) for (const wing of [-1, 1])
                pathData += line(tip, { x: tip.x + along.x * 5 * sign + outward.x * 2 * wing, y: tip.y + along.y * 5 * sign + outward.y * 2 * wing });
            label.path.setAttribute('d', pathData);
            const textCenter = shift(dimensionMiddle, label.height / 2 + textClearance + (compact ? 12 : 0));
            let angle = Math.atan2(dy, dx) * 180 / Math.PI; if (angle > 90) angle -= 180; if (angle < -90) angle += 180;
            label.node.style.left = `${textCenter.x}px`; label.node.style.top = `${textCenter.y}px`;
            label.node.style.transform = `translate(-50%,-50%) rotate(${angle}deg)`;
        }
    }
    return { reset, update, remeasure() {
        for (const label of labels) {
            const wasHidden = label.node.hidden; label.node.hidden = false;
            label.width = label.node.offsetWidth; label.height = label.node.offsetHeight; label.node.hidden = wasHidden;
        }
    }, dispose() { reset([]); svg.remove(); } };
}
