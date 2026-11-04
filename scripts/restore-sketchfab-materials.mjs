// Restore exported glTF materials from explicitly supplied Sketchfab settings.
// No network access; never overwrites an existing file. Requires ImageMagick.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import os from 'node:os';

const [input, optionsFile, textureIndexFile, output] = process.argv.slice(2);
if (!output || path.resolve(input) === path.resolve(output) || fs.existsSync(output)) {
    throw Error('usage: node scripts/restore-sketchfab-materials.mjs input.glb options.json textures.json NEW-output.glb');
}
const bytes = fs.readFileSync(input);
if (bytes.readUInt32LE(0) !== 0x46546c67 || bytes.readUInt32LE(4) !== 2) {
    throw Error('Expected GLB 2');
}
let json;
let binary;
for (let offset = 12; offset < bytes.length;) {
    const length = bytes.readUInt32LE(offset);
    const type = bytes.readUInt32LE(offset + 4);
    if (type === 0x4e4f534a) { json = JSON.parse(bytes.subarray(offset + 8, offset + 8 + length)); }
    if (type === 0x004e4942) { binary = bytes.subarray(offset + 8, offset + 8 + length); }
    offset += 8 + length;
}
if (!json || !binary || json.buffers.length !== 1) { throw Error('Expected single embedded buffer'); }
const source = JSON.parse(fs.readFileSync(optionsFile));
const textures = new Map(JSON.parse(fs.readFileSync(textureIndexFile)).results.map(t => [t.uid, t.name]));
const directory = path.join(path.dirname(input), 'textures');
const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'centrestage-restore-'));
const report = [];
function addImage(file) {
    const data = fs.readFileSync(file);
    const offset = (binary.length + 3) & ~3;
    binary = Buffer.concat([binary, Buffer.alloc(offset - binary.length), data]);
    json.bufferViews.push({ buffer: 0, byteOffset: offset, byteLength: data.length });
    json.images.push({ bufferView: json.bufferViews.length - 1, mimeType: 'image/png' });
    json.samplers ??= [];
    json.samplers.push({ magFilter: 9729, minFilter: 9987, wrapS: 10497, wrapT: 10497 });
    json.textures.push({ source: json.images.length - 1, sampler: json.samplers.length - 1 });
    return { index: json.textures.length - 1, texCoord: 0 };
}
function image(channel) {
    const name = textures.get(channel?.texture?.uid);
    if (!name || path.basename(name) !== name) { throw Error('Missing or unsafe texture name: ' + name); }
    const file = path.join(directory, name);
    if (!fs.existsSync(file)) { throw Error('Missing local source texture ' + file); }
    const uv = channel.UVTransforms;
    if ((channel.texture.texCoordUnit ?? 0) !== 0 || (uv && JSON.stringify(uv) !== JSON.stringify({scale:[1,1],offset:[0,0],rotation:0}))) {
        throw Error('Non-default UV transform requires explicit conversion');
    }
    return file;
}
let serial = 0;
function png(args) {
    const file = path.join(temp, `${serial++}.png`);
    execFileSync('magick', [...args, '-depth', '8', file]);
    return file;
}
try {
    for (const material of json.materials) {
        const matches = Object.values(source.materials).filter(m => m?.name === material.name);
        if (matches.length !== 1) { throw Error('Ambiguous/missing source material ' + material.name); }
        const c = matches[0].channels;
        const changes = [];
        const normal = c.NormalMap;
        if (normal?.enable && normal.texture) {
            material.normalTexture = { ...addImage(png([image(normal), ...(normal.flipY ? ['-channel', 'G', '-negate', '+channel'] : [])])), scale: normal.factor ?? 1 };
            changes.push('normal enable/strength/green convention');
        } else {
            delete material.normalTexture;
        }
        const diffuse = c.AlbedoPBR?.enable ? c.AlbedoPBR : c.DiffusePBR;
        if (diffuse?.enable) {
            const pbr = material.pbrMetallicRoughness ??= {};
            const factor = diffuse.factor ?? 1;
            const colour = diffuse.texture ? [1, 1, 1] : (diffuse.color ?? [1, 1, 1]);
            pbr.baseColorFactor = [...colour.map(v => v * factor), 1];
            if (diffuse.texture) { pbr.baseColorTexture = addImage(png([image(diffuse), '-alpha', 'off'])); }
            else { delete pbr.baseColorTexture; }
            const opacity = c.Opacity;
            if (opacity?.enable && opacity.type !== 'refraction') {
                pbr.baseColorFactor[3] = opacity.factor ?? 1;
                if (opacity.texture) {
                    // Opacity is explicitly ALPHA, not the RGB greyscale preview.
                    if (opacity.texture.internalFormat !== 'ALPHA') { throw Error('Unsupported opacity format'); }
                    const dimensions = execFileSync('magick', ['identify', '-format', '%wx%h', image(opacity)], { encoding: 'utf8' });
                    const alpha = png([image(opacity), '-alpha', 'extract', ...(opacity.invert ? ['-negate'] : [])]);
                    const base = diffuse.texture
                        ? png([image(diffuse), '-alpha', 'off', '-resize', dimensions + '!'])
                        : png(['-size', dimensions, 'xc:white']);
                    pbr.baseColorTexture = addImage(png([base, alpha, '-alpha', 'off', '-compose', 'CopyOpacity', '-composite']));
                    changes.push('source opacity packed at source alpha resolution');
                }
                material.alphaMode = 'BLEND';
                if (opacity.type === 'additive') { changes.push('WARNING additive approximated by alpha blend'); }
            }
            changes.push('source diffuse factor');
        }
        material.extensions ??= {};
        // Both models use Sketchfab's specular workflow, not a metallic texture.
        if (c.SpecularPBR?.enable) {
            const spec = c.SpecularPBR;
            const pbr = material.pbrMetallicRoughness;
            const gloss = c.GlossinessPBR?.enable ? c.GlossinessPBR.factor : 1 - (c.RoughnessPBR?.factor ?? 1);
            if ((c.GlossinessPBR?.enable && c.GlossinessPBR.texture && c.GlossinessPBR.factor !== 0) || (c.RoughnessPBR?.enable && c.RoughnessPBR.texture && c.RoughnessPBR.factor !== 0)) { throw Error('Gloss/roughness texture conversion is not implemented'); }
            const sg = { diffuseFactor: pbr.baseColorFactor, glossinessFactor: gloss,
                specularFactor: (spec.texture ? [1,1,1] : (spec.color ?? [1,1,1])).map(v => v * (spec.factor ?? 1)) };
            if (pbr.baseColorTexture) { sg.diffuseTexture = pbr.baseColorTexture; }
            if (spec.texture) {
                // A=1 so the scalar glossiness is not silently attenuated by PNG alpha.
                sg.specularGlossinessTexture = addImage(png([image(spec), '-alpha', 'on', '-channel', 'A', '-evaluate', 'set', '100%', '+channel']));
            }
            material.extensions.KHR_materials_pbrSpecularGlossiness = sg;
            pbr.metallicFactor = 0;
            delete pbr.metallicRoughnessTexture;
            changes.push('specular RGB texture/factor (specular-glossiness workflow)');
        }
        if (c.Opacity?.enable && c.Opacity.type === 'refraction') {
            material.extensions.KHR_materials_ior = { ior: c.Opacity.ior };
            material.extensions.KHR_materials_transmission = { transmissionFactor: 1 - c.Opacity.factor };
            // Refraction tint is explicitly white: not the unused diffuse preview.
            const tint = c.Opacity.useAlbedoTint ? material.pbrMetallicRoughness.baseColorFactor : [...c.Opacity.refractionColor, 1];
            material.pbrMetallicRoughness.baseColorFactor = tint;
            if (!c.Opacity.useAlbedoTint) { delete material.pbrMetallicRoughness.baseColorTexture; }
            const sg = material.extensions.KHR_materials_pbrSpecularGlossiness;
            if (sg) { sg.diffuseFactor = tint; if (!c.Opacity.useAlbedoTint) { delete sg.diffuseTexture; } }
            changes.push('source IOR and refraction tint; metallic disabled');
        }
        report.push({ material: material.name, changes });
    }
    json.extensionsUsed = [...new Set([...(json.extensionsUsed ?? []), ...json.materials.flatMap(m => Object.keys(m.extensions ?? {}))])];
    json.buffers[0].byteLength = binary.length;
    let manifest = Buffer.from(JSON.stringify(json));
    manifest = Buffer.concat([manifest, Buffer.alloc((4 - manifest.length % 4) % 4, 0x20)]);
    binary = Buffer.concat([binary, Buffer.alloc((4 - binary.length % 4) % 4)]);
    const header = Buffer.alloc(20);
    header.writeUInt32LE(0x46546c67); header.writeUInt32LE(2, 4);
    header.writeUInt32LE(28 + manifest.length + binary.length, 8);
    header.writeUInt32LE(manifest.length, 12); header.writeUInt32LE(0x4e4f534a, 16);
    const binHeader = Buffer.alloc(8);
    binHeader.writeUInt32LE(binary.length); binHeader.writeUInt32LE(0x004e4942, 4);
    fs.writeFileSync(output, Buffer.concat([header, manifest, binHeader, binary]), { flag: 'wx' });
    console.log(JSON.stringify(report, null, 2));
} finally {
    fs.rmSync(temp, { recursive: true, force: true });
}
