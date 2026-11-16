import {DefinitionError} from './expressions.mjs';
import {swMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';
import {reflectionMatrix,referencePlane,multiplyMatrix} from './feature-patterns.mjs';

const normalized=vector=>{const size=Math.hypot(...vector);if(size<1e-10)throw new DefinitionError('原生孔轴线无效');return vector.map(value=>value/size);};
const transformDirection=(vector,matrix)=>normalized([0,1,2].map(row=>vector.reduce((sum,value,index)=>sum+matrix[index*4+row]*value,0)));
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0);
const canonicalLine=(point,direction,radius)=>({origin:point.map((value,index)=>value-direction[index]*dot(point,direction)),direction,radius});
const equalLine=(left,right)=>Math.abs(Math.abs(dot(left.direction,right.direction))-1)<1e-10&&Math.hypot(...left.origin.map((value,index)=>value-right.origin[index]))<1e-5&&Math.abs(left.radius-right.radius)<1e-5;

// This resolves a finite mode choice, never a replacement shape from face data.
// Both source planes and the circular cut remain the runtime geometry inputs.
export function resolveDualMirrorMode(configuration,feature){
  const properties=feature.definition?.properties;
  const planes=configuration.features.filter(candidate=>candidate.type==='RefPlane'&&feature.parents.includes(candidate.name));
  if(feature.type!=='MirrorPattern'||planes.length!==2)throw new DefinitionError('镜像证据求解只适用于两个直接基准面引用');
  const first=planes.find(plane=>plane.id===properties.Plane?.id);
  const second=planes.find(plane=>plane.id!==first?.id);
  if(!first||!second?.referenceGeometry)throw new DefinitionError('两个镜像平面无法由原始引用确定');
  if(Math.abs(dot(referencePlane(properties.Plane).normal,referencePlane(second.referenceGeometry).normal))>1e-10)throw new DefinitionError('镜像模式证据只支持正交双平面');
  const seeds=properties.PatternFeatureArray;
  if(seeds?.length!==1)throw new DefinitionError('镜像模式证据需要唯一圆孔种子');
  const seed=configuration.features.find(candidate=>candidate.id===seeds[0].id&&candidate.type==='Cut');
  const profiles=configuration.features.filter(candidate=>candidate.sketch&&seed?.parents.includes(candidate.name));
  if(profiles.length!==1)throw new DefinitionError('镜像圆孔种子没有唯一原草图');
  const segments=profiles[0].sketch.segments.filter(segment=>!segment.construction);
  if(segments.length!==1||segments[0].type!==1||Math.hypot(...segments[0].geometry.start.map((value,index)=>value-segments[0].geometry.end[index]))>1e-10)throw new DefinitionError('镜像模式证据需要完整圆孔截面');
  const sketchToModel=inverseRigidMatrix(swMatrix(profiles[0].sketch.modelToSketch));
  const centre=transformPoint(segments[0].geometry.center.map(value=>value*1000),sketchToModel),axis=normalized(sketchToModel.slice(8,11)),radius=segments[0].geometry.radius*1000;
  const faces=feature.validation?.patternCylinderFaces;
  if(!faces?.length||faces.some(face=>face.oldestOwner?.id!==feature.id||face.patternSeed?.id!==seed.id||face.cylinderParameters?.length!==7))throw new DefinitionError('镜像孔面归属或原种子对应证据不足');
  const actual=faces.map(face=>{const cylinder=face.cylinderParameters;return canonicalLine(cylinder.slice(0,3).map(value=>value*1000),normalized(cylinder.slice(3,6)),cylinder[6]*1000);});
  if(actual.some((line,index)=>actual.slice(0,index).some(previous=>equalLine(line,previous))))throw new DefinitionError('镜像孔轴证据存在重复，无法唯一确定模式');
  const firstMatrix=reflectionMatrix(properties.Plane),secondMatrix=reflectionMatrix(second.referenceGeometry);
  const candidates=[true,false].filter(mirrorSeedOnly=>{
    const matrices=[firstMatrix,secondMatrix,...(mirrorSeedOnly?[]:[multiplyMatrix(secondMatrix,firstMatrix)])];
    const expected=matrices.map(matrix=>canonicalLine(transformPoint(centre,matrix),transformDirection(axis,matrix),radius));
    return expected.length===actual.length&&expected.every(line=>actual.filter(candidate=>equalLine(line,candidate)).length===1)&&actual.every(line=>expected.filter(candidate=>equalLine(line,candidate)).length===1);
  });
  if(candidates.length!==1)throw new DefinitionError('两个镜像模式无法由原特征孔轴唯一核验',{candidateCount:candidates.length});
  return {schemaVersion:'verified-dual-mirror/1',firstPlaneId:properties.Plane.id,secondPlane:second.referenceGeometry,seedFeatureIds:seeds.map(seed=>seed.id),mirrorSeedOnly:candidates[0],provenance:{method:'unique-mode-matching-feature-owned-cylinders',nativeSource:feature.validation.source,nativeCylinderCount:actual.length,matchingModes:candidates.length}};
}
