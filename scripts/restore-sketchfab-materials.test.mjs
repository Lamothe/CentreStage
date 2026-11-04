import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFileSync, spawnSync } from 'node:child_process';
const script = path.resolve('scripts/restore-sketchfab-materials.mjs');
function fixture(run) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cs-restore-test-'));
    try {
        fs.mkdirSync(path.join(dir, 'textures'));
        execFileSync('magick', ['-size','2x2','xc:rgba(100,50,20,0.25)',path.join(dir,'textures','base.png')]);
        const manifest = {asset:{version:'2.0'},buffers:[{byteLength:4}],bufferViews:[],images:[],textures:[],materials:[{name:'Skin',pbrMetallicRoughness:{}}]};
        let json=Buffer.from(JSON.stringify(manifest));
        json=Buffer.concat([json,Buffer.alloc((4-json.length%4)%4,32)]);
        const h=Buffer.alloc(20),b=Buffer.alloc(12);
        h.writeUInt32LE(0x46546c67);h.writeUInt32LE(2,4);h.writeUInt32LE(32+json.length,8);
        h.writeUInt32LE(json.length,12);h.writeUInt32LE(0x4e4f534a,16);
        b.writeUInt32LE(4);b.writeUInt32LE(0x004e4942,4);
        fs.writeFileSync(path.join(dir,'input.glb'),Buffer.concat([h,json,b]));
        fs.writeFileSync(path.join(dir,'textures.json'),JSON.stringify({results:[{uid:'base',name:'base.png'}]}));
        const channels={DiffusePBR:{enable:true,factor:0.4,texture:{uid:'base',internalFormat:'RGB'}},SpecularPBR:{enable:true,factor:0}};
        run(dir,channels);
    } finally { fs.rmSync(dir,{recursive:true,force:true}); }
}
function convert(dir, channels) {
    fs.writeFileSync(path.join(dir,'options.json'),JSON.stringify({materials:{one:{name:'Skin',channels}}}));
    return spawnSync(process.execPath,[script,...['input.glb','options.json','textures.json','out.glb'].map(f=>path.join(dir,f))],{encoding:'utf8'});
}
function read(dir) {
    const b=fs.readFileSync(path.join(dir,'out.glb')),n=b.readUInt32LE(12);
    return {json:JSON.parse(b.subarray(20,20+n)),bin:b.subarray(28+n)};
}
test('RGB diffuse clears unintended alpha; source factors and zero reflectance preserved',()=>fixture((d,c)=>{
    c.Opacity={enable:true,type:'alphaBlend',factor:0.7};
    const original=fs.readFileSync(path.join(d,'input.glb'));
    const r=convert(d,c);assert.equal(r.status,0,r.stderr);
    const {json,bin}=read(d),m=json.materials[0];
    assert.deepEqual(m.extensions.KHR_materials_pbrSpecularGlossiness.diffuseFactor,[0.4,0.4,0.4,0.7]);
    assert.deepEqual(m.extensions.KHR_materials_pbrSpecularGlossiness.specularFactor,[0,0,0]);
    const v=json.bufferViews[json.images[0].bufferView],f=path.join(d,'embedded.png');
    fs.writeFileSync(f,bin.subarray(v.byteOffset,v.byteOffset+v.byteLength));
    assert.equal(execFileSync('magick',[f,'-alpha','on','-format','%[fx:mean.a]','info:'],{encoding:'utf8'}),'1');
    assert.deepEqual(fs.readFileSync(path.join(d,'input.glb')),original);
    assert.notEqual(convert(d,c).status,0,'must reject overwrite');
}));
test('textured refraction tint retained only when requested',()=>fixture((d,c)=>{
    c.Opacity={enable:true,type:'refraction',factor:0,ior:1,useAlbedoTint:true,refractionColor:[1,1,1]};
    const r=convert(d,c);assert.equal(r.status,0,r.stderr);
    const {json}=read(d),m=json.materials[0];
    assert.ok(m.pbrMetallicRoughness.baseColorTexture);
    assert.ok(m.extensions.KHR_materials_pbrSpecularGlossiness.diffuseTexture);
    assert.equal(m.extensions.KHR_materials_ior.ior,1);
}));
test('opacity-only material gets white RGB with authored alpha',()=>fixture((d,c)=>{
    delete c.DiffusePBR.texture;
    c.Opacity={enable:true,type:'alphaBlend',factor:0.5,texture:{uid:'base',internalFormat:'ALPHA'}};
    const r=convert(d,c);assert.equal(r.status,0,r.stderr);
    const {json,bin}=read(d),i=json.textures[json.materials[0].pbrMetallicRoughness.baseColorTexture.index].source;
    const v=json.bufferViews[json.images[i].bufferView],f=path.join(d,'alpha.png');
    fs.writeFileSync(f,bin.subarray(v.byteOffset,v.byteOffset+v.byteLength));
    const a=Number(execFileSync('magick',[f,'-format','%[fx:mean.a]','info:'],{encoding:'utf8'}));
    assert.ok(a>0.24&&a<0.26);
}));
