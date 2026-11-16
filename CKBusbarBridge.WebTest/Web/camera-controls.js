// Adapted from BusbarToolsInWeb occtCameraControls.js; see docs/铜排三维说明.md.
import * as THREE from './lib/three/three.module.js';

// CAD gestures keep the picked pivot at its existing screen position while orbiting.
export class OcctCameraControls {
    constructor(camera, canvas, { pickPivot, getModelCenter, onChange }) {
        this.camera = camera;
        this.canvas = canvas;
        this.target = new THREE.Vector3();
        this.pickPivot = pickPivot;
        this.getModelCenter = getModelCenter;
        this.onChange = onChange;
        this.mouseDrag = null;
        this.touchPointers = new Map();
        this.touchSnapshot = null;
        this.pointerRaycaster = new THREE.Raycaster();
        this.pointerPlane = new THREE.Plane();
        this.listeners = {
            pointerdown: event => this.pointerDown(event),
            pointermove: event => this.pointerMove(event),
            pointerup: event => this.pointerEnd(event),
            pointercancel: event => this.pointerEnd(event),
            lostpointercapture: event => this.pointerEnd(event),
            wheel: event => this.mouseWheel(event),
            auxclick: event => { if (event.button === 1) event.preventDefault(); }
        };
        for (const [eventName, listener] of Object.entries(this.listeners))
            canvas.addEventListener(eventName, listener, { passive: false });
        this.onWindowBlur = () => this.cancelGestures();
        this.eventWindow = canvas.ownerDocument?.defaultView || window;
        this.eventWindow.addEventListener('blur', this.onWindowBlur);
    }

    gestureMode(event) {
        return event.ctrlKey || event.metaKey ? 'pan' : event.shiftKey ? 'zoom' : 'rotate';
    }

    resolvePivot(clientX, clientY) {
        const surfacePoint = this.pickPivot(clientX, clientY);
        if (surfacePoint) return surfacePoint.clone();
        // Empty space uses the cursor's position at the model's depth, never the world origin.
        const canvasBounds = this.canvas.getBoundingClientRect();
        const pointerPosition = new THREE.Vector2(
            (clientX - canvasBounds.left) / Math.max(canvasBounds.width, 1) * 2 - 1,
            -(clientY - canvasBounds.top) / Math.max(canvasBounds.height, 1) * 2 + 1);
        this.camera.updateMatrixWorld(true);
        this.pointerRaycaster.setFromCamera(pointerPosition, this.camera);
        this.pointerPlane.setFromNormalAndCoplanarPoint(this.camera.getWorldDirection(new THREE.Vector3()), this.getModelCenter());
        return this.pointerRaycaster.ray.intersectPlane(this.pointerPlane, new THREE.Vector3()) || this.target.clone();
    }

    pointerDown(event) {
        if (event.pointerType === 'touch') {
            this.touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
            this.touchSnapshot = this.readTouchSnapshot();
        } else {
            if (event.button !== 1 || this.mouseDrag) return;
            this.mouseDrag = { pointerId: event.pointerId, x: event.clientX, y: event.clientY,
                mode: this.gestureMode(event), pivot: this.resolvePivot(event.clientX, event.clientY) };
        }
        this.canvas.setPointerCapture(event.pointerId);
        event.preventDefault();
    }

    pointerMove(event) {
        if (this.touchPointers.has(event.pointerId)) {
            this.touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
            const nextTouchSnapshot = this.readTouchSnapshot();
            const previousTouchSnapshot = this.touchSnapshot;
            if (previousTouchSnapshot && nextTouchSnapshot.count === previousTouchSnapshot.count) {
                const touchDeltaX = nextTouchSnapshot.x - previousTouchSnapshot.x;
                const touchDeltaY = nextTouchSnapshot.y - previousTouchSnapshot.y;
                if (nextTouchSnapshot.count === 1) this.rotate(touchDeltaX, touchDeltaY, previousTouchSnapshot.pivot);
                else {
                    this.pan(touchDeltaX, touchDeltaY);
                    if (previousTouchSnapshot.distance > 0 && nextTouchSnapshot.distance > 0)
                        this.zoomAt(nextTouchSnapshot.distance / previousTouchSnapshot.distance,
                            this.resolvePivot(nextTouchSnapshot.x, nextTouchSnapshot.y));
                }
                nextTouchSnapshot.pivot = previousTouchSnapshot.pivot;
            }
            this.touchSnapshot = nextTouchSnapshot;
            event.preventDefault();
            return;
        }
        const activeDrag = this.mouseDrag;
        if (!activeDrag || activeDrag.pointerId !== event.pointerId) return;
        if ((event.buttons & 4) === 0) { this.pointerEnd(event); return; }
        const nextGestureMode = this.gestureMode(event);
        if (nextGestureMode !== activeDrag.mode) {
            activeDrag.mode = nextGestureMode;
            activeDrag.pivot = this.resolvePivot(activeDrag.x, activeDrag.y);
        }
        const dragDeltaX = event.clientX - activeDrag.x;
        const dragDeltaY = event.clientY - activeDrag.y;
        if (activeDrag.mode === 'pan') this.pan(dragDeltaX, dragDeltaY);
        else if (activeDrag.mode === 'zoom') this.zoomAt(Math.exp(-dragDeltaY * 0.01), activeDrag.pivot);
        else this.rotate(dragDeltaX, dragDeltaY, activeDrag.pivot);
        activeDrag.x = event.clientX;
        activeDrag.y = event.clientY;
        event.preventDefault();
    }

    pointerEnd(event) {
        if (this.mouseDrag?.pointerId === event.pointerId) this.mouseDrag = null;
        if (this.touchPointers.delete(event.pointerId)) this.touchSnapshot = this.readTouchSnapshot();
        if (this.canvas.hasPointerCapture(event.pointerId)) this.canvas.releasePointerCapture(event.pointerId);
    }

    readTouchSnapshot() {
        const activeTouches = [...this.touchPointers.values()];
        if (!activeTouches.length) return null;
        const firstTouch = activeTouches[0];
        const secondTouch = activeTouches[1] || firstTouch;
        const touchCenterX = (firstTouch.x + secondTouch.x) / 2;
        const touchCenterY = (firstTouch.y + secondTouch.y) / 2;
        return { count: activeTouches.length, x: touchCenterX, y: touchCenterY,
            distance: Math.hypot(secondTouch.x - firstTouch.x, secondTouch.y - firstTouch.y),
            pivot: this.resolvePivot(touchCenterX, touchCenterY) };
    }

    rotate(deltaX, deltaY, pivot) {
        if (deltaX === 0 && deltaY === 0) return;
        const radiansPerPixel = 2 * Math.PI / Math.max(this.canvas.clientHeight, 1);
        const cameraRight = new THREE.Vector3(1, 0, 0).applyQuaternion(this.camera.quaternion);
        const cameraUp = new THREE.Vector3(0, 1, 0).applyQuaternion(this.camera.quaternion);
        const horizontalRotation = new THREE.Quaternion().setFromAxisAngle(cameraUp, -deltaX * radiansPerPixel);
        const verticalRotation = new THREE.Quaternion().setFromAxisAngle(cameraRight, -deltaY * radiansPerPixel);
        const orbitRotation = horizontalRotation.multiply(verticalRotation);
        this.camera.position.sub(pivot).applyQuaternion(orbitRotation).add(pivot);
        this.camera.quaternion.premultiply(orbitRotation).normalize();
        this.camera.up.applyQuaternion(orbitRotation).normalize();
        this.target.sub(pivot).applyQuaternion(orbitRotation).add(pivot);
        this.changed(true);
    }

    pan(deltaX, deltaY) {
        if (deltaX === 0 && deltaY === 0) return;
        const canvasBounds = this.canvas.getBoundingClientRect();
        const unitsPerPixelX = (this.camera.right - this.camera.left) / (this.camera.zoom * Math.max(canvasBounds.width, 1));
        const unitsPerPixelY = (this.camera.top - this.camera.bottom) / (this.camera.zoom * Math.max(canvasBounds.height, 1));
        const cameraTranslation = new THREE.Vector3(-deltaX * unitsPerPixelX, deltaY * unitsPerPixelY, 0)
            .applyQuaternion(this.camera.quaternion);
        this.camera.position.add(cameraTranslation);
        this.target.add(cameraTranslation);
        this.changed();
    }

    zoomAt(zoomFactor, anchor) {
        const nextZoom = THREE.MathUtils.clamp(this.camera.zoom * zoomFactor, 0.0001, 10000);
        if (nextZoom === this.camera.zoom) return;
        // Preserve the anchor's projection instead of pulling it to the viewport center.
        this.camera.updateMatrixWorld(true);
        const anchorProjection = anchor.clone().project(this.camera);
        this.camera.zoom = nextZoom;
        this.camera.updateProjectionMatrix();
        const cameraTranslation = anchor.clone().sub(anchorProjection.unproject(this.camera));
        this.camera.position.add(cameraTranslation);
        this.target.add(cameraTranslation);
        this.changed();
    }

    mouseWheel(event) {
        event.preventDefault();
        const deltaInPixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? this.canvas.clientHeight : 1);
        this.zoomAt(Math.exp(-THREE.MathUtils.clamp(deltaInPixels, -2000, 2000) * 0.001), this.resolvePivot(event.clientX, event.clientY));
    }

    changed(rotated = false) {
        this.camera.updateMatrixWorld(true);
        this.onChange(rotated);
    }

    cancelGestures() {
        const capturedPointers = [...this.touchPointers.keys()];
        if (this.mouseDrag) capturedPointers.push(this.mouseDrag.pointerId);
        this.mouseDrag = null;
        this.touchPointers.clear();
        this.touchSnapshot = null;
        for (const pointerId of capturedPointers)
            if (this.canvas.hasPointerCapture(pointerId)) this.canvas.releasePointerCapture(pointerId);
    }

    setWindow(targetWindow) {
        this.cancelGestures();
        this.eventWindow.removeEventListener('blur', this.onWindowBlur);
        this.eventWindow = targetWindow;
        this.eventWindow.addEventListener('blur', this.onWindowBlur);
    }

    dispose() {
        this.cancelGestures();
        for (const [eventName, listener] of Object.entries(this.listeners)) this.canvas.removeEventListener(eventName, listener);
        this.eventWindow.removeEventListener('blur', this.onWindowBlur);
    }
}
