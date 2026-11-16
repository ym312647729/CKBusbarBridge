import {DefinitionError} from './expressions.mjs';
import {stitchPreviewFlangeEnds} from './preview-seams.mjs';
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0);
const difference=(left,right)=>left.map((value,index)=>value-right[index]);
const unit=vector=>{const length=Math.hypot(...vector);if(length<1e-10)throw new DefinitionError('法兰引用方向无效');return vector.map(value=>value/length);};

export function edgeFlangeOperations(kernel,feature,configuration,geometry,arcSegments=24){
  const properties=feature.definition?.properties;
  if(!properties||properties.PositionType!==1||properties.OffsetType!==1||properties.UsePositionOffset||properties.AngleReference||Math.abs(properties.BendAngle-Math.PI/2)>1e-8)throw new DefinitionError('该法兰位置、角度或终止条件尚未支持：'+feature.name);
  const edges=properties.Edges;
  if(!edges?.length||edges.some(edge=>!edge.start||!edge.end))throw new DefinitionError('法兰缺少原始选择边：'+feature.name);
  const firstDirection=unit(difference(edges[0].end,edges[0].start));
  const schematicCorners=properties.UsePositionTrimSideBends||edges.some(edge=>Math.abs(Math.abs(dot(unit(difference(edge.end,edge.start)),firstDirection))-1)>1e-8);
  const sourceSheets=configuration.features.filter(candidate=>!candidate.suppressed&&candidate.type==='SheetMetal'&&feature.parents.includes(candidate.name));
  const profiles=configuration.features.filter(candidate=>candidate.parent===feature.name&&candidate.sketch&&!candidate.suppressed);
  if(sourceSheets.length!==1||profiles.length!==edges.length)throw new DefinitionError('法兰未能唯一对应源钣金与轮廓：'+feature.name);
  const thickness=sourceSheets[0].definition?.properties.Thickness*1000,radius=properties.BendRadius*1000;
  if(!(thickness>0&&radius>0))throw new DefinitionError('法兰厚度或半径缺失：'+feature.name);
  const operations=[],matchedEdges=new Set();
  try{
    for(const profile of profiles){
      const sketchToModel=geometry.inverseRigidMatrix(geometry.swMatrix(profile.sketch.modelToSketch));
      const along=unit(sketchToModel.slice(0,3)),rise=unit(sketchToModel.slice(4,7)),profileNormal=unit(sketchToModel.slice(8,11)),origin=sketchToModel.slice(12,15);
      const candidates=edges.filter(edge=>edge.lineParameters&&edge.start&&edge.end&&[edge.start,edge.end].every(point=>Math.abs(dot(difference(point.map(value=>value*1000),origin),profileNormal))<1e-5)&&Math.abs(Math.abs(dot(unit(difference(edge.end,edge.start)),along))-1)<1e-8);
      if(candidates.length!==1)throw new DefinitionError('法兰草图无法唯一对应原始选择边：'+profile.name);
      const edge=candidates[0];
      if(matchedEdges.has(edge))throw new DefinitionError('多个法兰草图对应同一条源边：'+feature.name);
      matchedEdges.add(edge);
      if(edge.adjacentFaces?.length!==2||edge.adjacentFaces.some(face=>!face.planeParameters))throw new DefinitionError('法兰选择边缺少两个原始平面引用：'+feature.name);
      const inwardNormals=edge.adjacentFaces.map(face=>unit(face.planeParameters.slice(0,3)).map(value=>value*(face.sense?1:-1)));
      const sideNormals=inwardNormals.filter(normal=>Math.abs(Math.abs(dot(normal,profileNormal))-1)<1e-8);
      const baseNormals=inwardNormals.filter(normal=>Math.abs(Math.abs(dot(normal,rise))-1)<1e-8);
      if(sideNormals.length!==1||baseNormals.length!==1)throw new DefinitionError('法兰原始邻面与草图坐标不一致：'+feature.name);
      const inward=sideNormals[0],baseNormal=baseNormals[0],outerRadius=radius+thickness;
      const edgeCoordinates=[edge.start,edge.end].map(point=>{
        const delta=difference(point.map(value=>value*1000),origin);
        return {u:dot(delta,along),x:dot(delta,inward),y:dot(delta,rise)};
      });
      const edgeLower=Math.min(...edgeCoordinates.map(point=>point.u)),edgeUpper=Math.max(...edgeCoordinates.map(point=>point.u));
      if(edgeCoordinates.some(point=>Math.abs(point.x)>1e-5||Math.abs(Math.min(point.y,point.y+dot(baseNormal,rise)*thickness)+thickness)>1e-5))throw new DefinitionError('法兰草图未位于原生内表面基线：'+profile.name);
      const segments=profile.sketch.segments.filter(segment=>!segment.construction);
      const baseline=segments.filter(segment=>segment.type===0&&Math.abs(segment.geometry.start[1])<1e-8&&Math.abs(segment.geometry.end[1])<1e-8);
      if(baseline.length!==1||segments.some(segment=>segment.geometry.start[1]<-1e-8||segment.geometry.end[1]<-1e-8))throw new DefinitionError('法兰原始轮廓基线不完整：'+profile.name);
      let flangeLower=Math.min(baseline[0].geometry.start[0],baseline[0].geometry.end[0])*1000,flangeUpper=Math.max(baseline[0].geometry.start[0],baseline[0].geometry.end[0])*1000;
      if(flangeLower<edgeLower-1e-5||flangeUpper>edgeUpper+1e-5||flangeUpper-flangeLower<1e-5)throw new DefinitionError('法兰轮廓超出原始选择边：'+profile.name);
      const stitching=stitchPreviewFlangeEnds(geometry.sketchLoops(profile.sketch,arcSegments),flangeLower,flangeUpper,edgeLower,edgeUpper);
      flangeLower=stitching.lower;flangeUpper=stitching.upper;
      if(stitching.stitched.length)(operations.stitchedSlits??=[]).push({profile:profile.name,ends:stitching.stitched});
      // Material-inside bends replace the original edge strip. The captured profile fixes relief end positions.
      const crossPlacement=offset=>[...inward,0,...rise,0,...along,0,...origin.map((value,index)=>value+along[index]*offset),1];
      const removalSection=new kernel.CrossSection([[[0,-thickness],[outerRadius,-thickness],[outerRadius,0],[0,0]]],'EvenOdd');
      try{const removal=removalSection.extrude(edgeUpper-edgeLower);try{operations.push({kind:'subtract',solid:removal.transform(crossPlacement(edgeLower))});}finally{removal.delete();}}finally{removalSection.delete();}
      const steps=Math.max(6,Math.ceil(arcSegments/4));
      const sector=[];
      for(let index=0;index<=steps;index++){const angle=Math.PI+Math.PI/2*index/steps;sector.push([outerRadius+outerRadius*Math.cos(angle),radius+outerRadius*Math.sin(angle)]);}
      for(let index=steps;index>=0;index--){const angle=Math.PI+Math.PI/2*index/steps;sector.push([outerRadius+radius*Math.cos(angle),radius+radius*Math.sin(angle)]);}
      const bendSection=new kernel.CrossSection([sector],'EvenOdd');
      try{const bend=bendSection.extrude(flangeUpper-flangeLower);try{operations.push({kind:'add',solid:bend.transform(crossPlacement(flangeLower))});}finally{bend.delete();}}finally{bendSection.delete();}
      const flatSection=new kernel.CrossSection(stitching.loops,'EvenOdd');
      try{
        const flat=flatSection.extrude(thickness),positioned=flat.translate([0,0,dot(profileNormal,inward)>0?0:-thickness]);
        flat.delete();
        try{const placed=positioned.transform(sketchToModel);try{operations.push({kind:'add',solid:placed.trimByPlane(rise,dot(rise,origin)+radius)});}finally{placed.delete();}}finally{positioned.delete();}
      }finally{flatSection.delete();}
    }
    // User-approved schematic corner treatment: retain the actual flange
    // profiles, thickness and radius, without reproducing native corner relief.
    // Keep this identity with the result; it is not manufacturing geometry.
    if(schematicCorners)operations.previewApproximation={kind:'sheet-metal-corners',trimSideBends:properties.UsePositionTrimSideBends===true,gapMm:properties.GapDistance*1000,method:'independent-native-flange-profiles'};
    return operations;
  }catch(error){operations.forEach(operation=>operation.solid.delete());throw error;}
}
