import {DefinitionError} from './expressions.mjs';
import {transformPoint} from './geometry.mjs';

// This one captured cut has an inconsistent transverse annotation: D1=40,
// while its two real ends are both 40 mm from the member ends. Retain its
// captured section and use that proven end offset for the approved base
// illustration. The original dimension and equations remain in design JSON.
export function driveSchematicBaseCut(path,profile,oldEnds,newEnds,toModel,toSketch,dimensionOverrides){
  if(path!=='模板/基座模板/侧对侧基座1/8GX.160.155.SLDPRT'||profile.name!=='草图2')return null;
  const sketch=profile.sketch,sourceLength=Math.hypot(...oldEnds[1].map((value,axis)=>value-oldEnds[0][axis])),newLength=Math.hypot(...newEnds[1].map((value,axis)=>value-newEnds[0][axis])),axis=oldEnds[1].map((value,index)=>(value-oldEnds[0][index])/sourceLength),canonical=name=>name.split('@').slice(0,-1).join('@'),offsetDimension=profile.dimensions.find(row=>canonical(row.name)==='D1@草图2'),sectionDimension=profile.dimensions.find(row=>canonical(row.name)==='D2@草图2'),trim=offsetDimension?.value*1000;
  const fail=()=>{throw new DefinitionError('侧对侧基座示意切口不再符合已存两端40 mm修剪轮廓');};
  if(!offsetDimension||!sectionDimension||sketch.segments.some(segment=>segment.type!==0)||newLength<=2*trim||Object.entries(dimensionOverrides).some(([name,value])=>Math.abs(value-(profile.dimensions.find(row=>canonical(row.name)===name)?.value*1000))>1e-6))fail();
  const station=point=>point.reduce((sum,value,index)=>sum+(value-oldEnds[0][index])*axis[index],0),endStations=[trim,sourceLength-trim],map=point=>{const world=transformPoint(point.map(value=>value*1000),toModel),position=station(world),end=endStations.findIndex(value=>Math.abs(position-value)<1e-5);if(end<0&&Math.abs(position-sourceLength/2)>1e-5)fail();const shift=end<0?[0,0,0]:newEnds[end].map((value,index)=>value-oldEnds[end][index]);return transformPoint(world.map((value,index)=>value+shift[index]),toSketch).map(value=>value/1000);};
  const physical=sketch.segments.filter(segment=>!segment.construction);if(physical.length!==4||physical.some(segment=>[segment.geometry.start,segment.geometry.end].some(point=>!endStations.some(value=>Math.abs(station(transformPoint(point.map(value=>value*1000),toModel))-value)<1e-5))))fail();
  const updated=structuredClone(sketch);for(const segment of updated.segments){segment.geometry.start=map(segment.geometry.start);segment.geometry.end=map(segment.geometry.end);}for(const point of updated.points)point.coordinates=map(point.coordinates);for(const relation of updated.relations)for(const entity of relation.entities)if(entity?.point&&updated.points.some(point=>point.id===entity.id))entity.point=[...updated.points.find(point=>point.id===entity.id).coordinates];
  return {sketch:updated,evidence:{kind:'schematic-structure-cut',feature:profile.name,source:'captured-constant-section-and-proven-member-end-trims',endTrimMm:trim,originalDimensionPreserved:true,annotationConflict:'D1@草图2 原记录为40 mm；其横向测量与捕获几何不一致，预览沿两端实际40 mm修剪驱动',nativeModelValidationRequired:false}};
}
