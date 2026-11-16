// Render SolidWorks toolbar resources from the repository's SVGs.
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const moduleFlag = process.argv.indexOf('--sharp-module');
const sharp = require(moduleFlag < 0 ? 'sharp' : path.resolve(process.argv[moduleFlag + 1]));
const checkOnly = process.argv.includes('--check');
const plugin = path.join(__dirname, '..', 'CKBusbarBridge');
const icons = path.join(plugin, 'icons');
const sizes = [20, 32, 40, 64, 96, 128];

async function write(file, expected) {
    if (checkOnly) {
        const actual = await fs.readFile(file);
        if (file.endsWith('.png')) {
            const pixels = bytes => sharp(bytes).raw().toBuffer({ resolveWithObject: true });
            assert.deepEqual(await pixels(actual), await pixels(expected), path.basename(file) + ' needs regeneration');
        } else assert.deepEqual(actual, expected, path.basename(file) + ' needs regeneration');
    } else await fs.writeFile(file, expected);
}

const render = (source, size) => sharp(source, { density: 288 }).resize(size, size).png().toBuffer();
const empty = (width, height) => sharp({ create: { width, height, channels: 4, background: '#00000000' } });

async function legacyBitmap(source) {
    const size = 16;
    const rgb = await sharp(await render(source, size)).flatten({ background: '#C0C0C0' }).raw().toBuffer();
    const stride = size * 3; // 16 × RGB is already DWORD aligned.
    const bmp = Buffer.alloc(54 + stride * size);
    bmp.write('BM'); bmp.writeUInt32LE(bmp.length, 2); bmp.writeUInt32LE(54, 10);
    bmp.writeUInt32LE(40, 14); bmp.writeInt32LE(size, 18); bmp.writeInt32LE(size, 22);
    bmp.writeUInt16LE(1, 26); bmp.writeUInt16LE(24, 28); bmp.writeUInt32LE(stride * size, 34);
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
        const from = y * stride + x * 3, to = 54 + (size - 1 - y) * stride + x * 3;
        bmp[to] = rgb[from + 2]; bmp[to + 1] = rgb[from + 1]; bmp[to + 2] = rgb[from];
    }
    return bmp;
}

async function main() {
    const manifest = JSON.parse(await fs.readFile(path.join(icons, 'toolbar-icons.json'), 'utf8'));
    const addin = await fs.readFile(path.join(plugin, 'SwAddin.cs'), 'utf8');
    const project = await fs.readFile(path.join(plugin, 'CKBusbarBridge.csproj'), 'utf8');
    const commands = [...addin.matchAll(/\bAdd\("([^"]+)",\s*(\d+),\s*nameof\(/g)];
    assert.equal(commands.length, manifest.filter(icon => !icon.reserved).length);
    const sources = [];
    for (const [index, icon] of manifest.entries()) {
        assert.equal(icon.index, index, 'Icon strip indices must be contiguous');
        const command = commands.find(entry => Number(entry[2]) === index);
        assert.equal(command?.[1], icon.reserved ? undefined : icon.label, 'Icon slot ' + index);
        sources.push(await fs.readFile(path.join(icons, 'src', icon.name + '.svg')));
    }
    const bridge = manifest.findIndex(icon => icon.name === 'bridge-design');
    assert.ok(bridge >= 0);
    const strips = new Map();
    for (const size of sizes) {
        const cells = await Promise.all(sources.map(source => render(source, size)));
        assert.equal(new Set(cells.map(cell => cell.toString('base64'))).size, cells.length, size + 'px has duplicate icons');
        const strip = await empty(size * cells.length, size)
            .composite(cells.map((input, index) => ({ input, left: index * size, top: 0 }))).png().toBuffer();
        for (const [name, buffer] of [['toolbar' + size + 'x.png', strip], ['mainicon_' + size + '.png', cells[bridge]]]) {
            assert.ok(project.includes('Include="icons\\' + name + '"'), name + ' is not embedded');
            await write(path.join(icons, name), buffer);
        }
        strips.set(size, strip);
    }
    const preview = await sharp({ create: { width: 680, height: 180, channels: 4, background: '#F4F7FC' } })
        .composite([{ input: strips.get(64), left: 20, top: 16 }, { input: strips.get(32), left: 20, top: 94 },
            { input: strips.get(20), left: 20, top: 142 }]).png().toBuffer();
    await write(path.join(icons, 'preview.png'), preview);
    const bmp = await legacyBitmap(sources[bridge]);
    await write(path.join(icons, 'AddinIcon.bmp'), bmp);
    await write(path.join(plugin, 'AddinIcon.bmp'), bmp);
    console.log((checkOnly ? 'Verified' : 'Generated') + ' 10 toolbar icons at 6 sizes and the legacy bitmap.');
}

main().catch(error => { console.error(error); process.exitCode = 1; });
