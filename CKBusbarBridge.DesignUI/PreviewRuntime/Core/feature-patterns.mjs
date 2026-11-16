import {DefinitionError} from './expressions.mjs';

export const identityMatrix=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
export function multiplyMatrix(left,right){const result=Array(16).fill(0);for(let column=0;column<4;column++)for(let row=0;row<4;row++)for(let k=0;k<4;k++)result[column*4+row]+=left[k*4+row]*right[column*4+k];return result;}
const normalized=vector=>{const magnitude=Math.hypot(...vector);if(!(magnitude>1e-12))throw new DefinitionError('特征引用方向无效');return vector.map(value=>value/magnitude);};
export function referencePlane(reference){
  if(reference?.planeParameters){const parameters=reference.planeParameters;return {normal:normalized(parameters.slice(0,3)),origin:parameters.slice(3,6).map(value=>value*1000)};}
  const transform=reference?.referencePlaneTransform;
  if(transform?.length===16)return {normal:normalized(transform.slice(6,9)),origin:transform.slice(9,12).map(value=>value*1000)};
  throw new DefinitionError('特征引用平面没有原始数学定义');
}
export function reflectionMatrix(reference){
  const {normal,origin}=referencePlane(reference),offset=normal.reduce((sum,value,index)=>sum+value*origin[index],0);
  const matrix=identityMatrix.slice();
  for(let column=0;column<3;column++)for(let row=0;row<3;row++)matrix[column*4+row]=(row===column?1:0)-2*normal[row]*normal[column];
  for(let axis=0;axis<3;axis++)matrix[12+axis]=2*offset*normal[axis];
  return matrix;
}
export function patternOperations(feature,history,convertTransform){
  const properties=feature.definition?.properties;
  if(!properties)throw new DefinitionError('阵列/镜像定义缺失：'+feature.name);
  if(properties.BodyPattern||properties.PatternBodyArray?.length||properties.PatternFaceArray?.length||properties.MirrorFaceArray?.length)throw new DefinitionError('实体/面阵列还没有可核验的源实体选择：'+feature.name);
  if(properties.VarySketch||properties.InstancesToVary)throw new DefinitionError('变化实例阵列尚未完成参数重求值：'+feature.name);
  const seeds=properties.PatternFeatureArray;
  if(!seeds?.length)throw new DefinitionError('阵列没有源特征选择：'+feature.name);
  const seedOperations=seeds.flatMap(seed=>{
    const operations=history.get(seed.id)??history.get(seed.feature);
    if(!operations)throw new DefinitionError('阵列种子特征尚未生成：'+seed.feature);
    return operations;
  });
  let matrices;
  if(feature.type==='MirrorPattern'){
    const first=reflectionMatrix(properties.Plane),secondary=properties.secondaryMirrorDefinition;
    matrices=[first];
    if(secondary){
      if(secondary.schemaVersion!=='verified-dual-mirror/1'||typeof secondary.mirrorSeedOnly!=='boolean'||secondary.firstPlaneId!==properties.Plane.id||secondary.seedFeatureIds.length!==seeds.length||!seeds.every(seed=>secondary.seedFeatureIds.includes(seed.id)))throw new DefinitionError('第二镜像平面的模式记录与原选择不一致：'+feature.name);
      const second=reflectionMatrix(secondary.secondPlane);
      matrices.push(second);
      if(!secondary.mirrorSeedOnly)matrices.push(multiplyMatrix(second,first));
    }
  }
  else{
    const count=properties.D1TotalInstances*Math.max(1,properties.D2TotalInstances);
    if(properties.instanceTransforms?.length!==count||!properties.instanceTransforms.every(Array.isArray))throw new DefinitionError('线性阵列实例变换不完整：'+feature.name);
    const skipped=new Set(properties.SkippedItemArray??[]);
    matrices=properties.instanceTransforms.flatMap((transform,index)=>index===0||skipped.has(index)?[]:[convertTransform(transform)]);
  }
  return matrices.flatMap(matrix=>seedOperations.map(operation=>({kind:operation.kind,solid:operation.solid.transform(matrix)})));
}
