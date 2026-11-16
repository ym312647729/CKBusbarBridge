// Round editable values and labels without changing untouched CAD geometry.
export function roundParameter(value) {
    if (!Number.isFinite(value) || value === 0) return value === 0 ? 0 : value;
    const magnitude = Math.abs(value);
    if (magnitude < .1) return Number(value.toPrecision(2));
    const [coefficient, exponent = '0'] = magnitude.toString().split('e');
    const shifted = Number(`${coefficient}e${Number(exponent) + 1}`);
    return Number.isFinite(shifted) ? Math.sign(value) * Number(`${Math.round(shifted)}e-1`) : value;
}

export function formatParameter(value) {
    if (!Number.isFinite(value)) return '';
    const rounded = roundParameter(value), magnitude = Math.abs(rounded);
    const decimals = magnitude > 0 && magnitude < .1 ? Math.min(100, 1 - Math.floor(Math.log10(magnitude))) : 1;
    return Number.isInteger(rounded) ? rounded.toFixed(0) : rounded.toFixed(decimals);
}

const formattedInputs = new WeakSet();
export function setParameterInput(control, value) {
    control.value = formatParameter(value);
    if (formattedInputs.has(control)) return;
    formattedInputs.add(control);
    control.addEventListener('change', () => {
        if (Number.isFinite(control.valueAsNumber)) control.value = formatParameter(control.valueAsNumber);
    });
}
