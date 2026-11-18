const fieldTypes={positions:Float32Array,edgePositions:Float32Array,quads:Uint32Array,triangles:Uint32Array,edges:Uint32Array};

// 只压缩已交付网格的表示；Float32 坐标和 Uint32 索引保持原值，不重新离散。
export function compactMeshResponse(response){
  const supplied=new Set();let shapes=0,references=0,rawNumbers=0;
  for(const rows of [response.instances,response.interactionInstances])for(const row of rows??[]){
    if(!Object.hasOwn(row,'positions'))continue;
    if(typeof row.geometryKey!=='string'||typeof row.geometrySignature!=='string')throw new Error('网格传输缺少稳定身份');
    const identity=row.geometryKey+'\0'+row.geometrySignature;
    if(supplied.has(identity)){
      for(const name of Object.keys(fieldTypes))delete row[name];
      references++;continue;
    }
    supplied.add(identity);
    const packed={};
    for(const [name,Type]of Object.entries(fieldTypes))if(Object.hasOwn(row,name)){
      const values=row[name];
      if(!Array.isArray(values)||values.some(value=>!Number.isFinite(value)||
        (Type===Uint32Array&&(!Number.isInteger(value)||value<0||value>0xffffffff))||
        (Type===Float32Array&&!Object.is(Math.fround(value),value)&&value!==0)))
        throw new Error('网格传输包含非法或非原精度数据：'+name);
      const typed=Type.from(values);
      packed[name]=Buffer.from(typed.buffer,typed.byteOffset,typed.byteLength).toString('base64');
      rawNumbers+=values.length;delete row[name];
    }
    row.packedGeometry={encoding:'mesh32-le/1',fields:packed};shapes++;
  }
  return {shapes,references,rawNumbers};
}
