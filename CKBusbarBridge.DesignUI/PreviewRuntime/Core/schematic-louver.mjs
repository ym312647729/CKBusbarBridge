import {DefinitionError} from './expressions.mjs';

// User-approved rectangular illustration, limited to the recorded louver feature.
export function louverOperations(kernel,feature,sketch,thickness,loops,placement){
  const definition=feature.schematicDefinition;
  if(feature.representation?.mode!=='schematic'||definition?.kind!=='raised-louver')throw new DefinitionError('成形特征没有用户批准的示意定义：'+feature.name);
  if(!(thickness>0&&definition.heightMm>thickness))throw new DefinitionError('百叶窗示意的源板厚或高度无效：'+feature.name);
  const sourceLines=sketch.segments.filter(segment=>!segment.construction&&segment.type===0);
  if(sourceLines.length!==2)throw new DefinitionError('百叶窗开口边的原始轮廓无法唯一确定：'+feature.name);
  const lines=sourceLines.map(segment=>{
    const start=segment.geometry.start.slice(0,2).map(value=>value*1000),end=segment.geometry.end.slice(0,2).map(value=>value*1000);
    return {start,end,length:Math.hypot(end[0]-start[0],end[1]-start[1]),midpoint:start.map((value,index)=>(value+end[index])/2)};
  }).sort((left,right)=>right.length-left.length);
  const mouth=lines[0],back=lines[1],separation=back.midpoint.map((value,index)=>value-mouth.midpoint[index]),span=Math.hypot(...separation);
  if(!(span>0&&mouth.length>back.length))throw new DefinitionError('百叶窗开口与后缘定义无效：'+feature.name);
  const normal=separation.map(value=>value/span),height=definition.heightMm;
  if(Math.abs(normal[0]*(mouth.end[0]-mouth.start[0])+normal[1]*(mouth.end[1]-mouth.start[1]))>1e-4)throw new DefinitionError('百叶窗示意只接受原始平行开口边：'+feature.name);
  const tangent=mouth.end.map((value,index)=>(value-mouth.start[index])/mouth.length),perpendicular=[-tangent[1],tangent[0]],points=loops.flat();
  const along=points.map(point=>point[0]*tangent[0]+point[1]*tangent[1]),across=points.map(point=>point[0]*perpendicular[0]+point[1]*perpendicular[1]);
  const minAlong=Math.min(...along),maxAlong=Math.max(...along),minAcross=Math.min(...across),maxAcross=Math.max(...across);
  if(![minAlong,maxAlong,minAcross,maxAcross].every(Number.isFinite)||!(maxAlong>minAlong&&maxAcross>minAcross))throw new DefinitionError('百叶窗矩形示意缺少有效的原轮廓：'+feature.name);
  const footprintFrame=[...tangent,0,0,...perpendicular,0,0,0,0,1,0,tangent[0]*minAlong+perpendicular[0]*minAcross,tangent[1]*minAlong+perpendicular[1]*minAcross,0,1];
  const temporary=[],operations=[];
  try{
    const block=kernel.Manifold.cube([maxAlong-minAlong,maxAcross-minAcross,height]);temporary.push(block);
    const localBlock=block.transform(footprintFrame);temporary.push(localBlock);
    operations.push({kind:'add',solid:localBlock.transform(placement)});
    return operations;
  }catch(error){operations.forEach(operation=>operation.solid.delete());throw error;}
  finally{temporary.forEach(solid=>solid.delete());}
}
