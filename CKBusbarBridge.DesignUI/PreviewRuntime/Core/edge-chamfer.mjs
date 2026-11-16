import {DefinitionError} from './expressions.mjs';
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0);
const unit=vector=>{const length=Math.hypot(...vector);if(!(length>0))throw new DefinitionError('倒角引用方向无效');return vector.map(value=>value/length);};

export function chamferOperations(kernel,feature){
  const properties=feature.definition?.properties;
  if(properties?.Type!==1||Math.abs(properties.EdgeChamferAngle-Math.PI/4)>1e-8)throw new DefinitionError('仅已核验的45度距离倒角已支持：'+feature.name);
  if(properties.Faces?.length||properties.Loops?.length||properties.Vertex||!properties.Edges?.length)throw new DefinitionError('倒角需要完整直边选择：'+feature.name);
  const distance=properties.edgeChamferDistances?.[0]*1000;
  if(!(distance>0))throw new DefinitionError('倒角距离缺失：'+feature.name);
  const operations=[];
  try{
    for(const edge of properties.Edges){
      if(!edge.lineParameters||!edge.start||!edge.end||edge.adjacentFaces?.length!==2||!edge.adjacentFaces.every(face=>face.planeParameters))throw new DefinitionError('曲边或非平面倒角尚未支持：'+feature.name);
      const inwardNormals=edge.adjacentFaces.map(face=>unit(face.planeParameters.slice(0,3)).map(value=>value*(face.sense?1:-1)));
      if(Math.abs(dot(...inwardNormals))>1e-8)throw new DefinitionError('非直角邻面倒角尚未支持：'+feature.name);
      const start=edge.start.map(value=>value*1000),direction=edge.end.map((value,index)=>value*1000-start[index]),length=Math.hypot(...direction),axis=unit(direction);
      if(inwardNormals.some(normal=>Math.abs(dot(normal,axis))>1e-8))throw new DefinitionError('倒角边与邻面引用不一致：'+feature.name);
      const placement=[...inwardNormals[0],0,...inwardNormals[1],0,...axis,0,...start,1];
      const section=new kernel.CrossSection([[[0,0],[distance,0],[0,distance]]],'EvenOdd');
      try{const wedge=section.extrude(length);try{operations.push({kind:'subtract',solid:wedge.transform(placement)});}finally{wedge.delete();}}finally{section.delete();}
    }
    return operations;
  }catch(error){operations.forEach(operation=>operation.solid.delete());throw error;}
}
