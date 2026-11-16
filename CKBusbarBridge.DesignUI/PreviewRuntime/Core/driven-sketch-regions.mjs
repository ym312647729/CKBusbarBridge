import {DefinitionError} from './expressions.mjs';
import {resolveSketchRegion} from './selected-regions.mjs';

const near=(first,second)=>Math.hypot(first[0]-second[0],first[1]-second[1])<1e-9;
const toModel=(point,matrix)=>[0,1,2].map(axis=>[0,1,2].reduce((sum,index)=>sum+matrix[axis*3+index]*(point[index]-matrix[9+index]),0));

// Rebuild only selections proven to contain complete original sketch curves.
// A trimmed/intersected region requires its own topology solver and is rejected.
export function driveCompleteSketchRegions(original,updated,selections){
  if(!selections?.length)return {selections,evidence:[]};
  if(!original.modelToSketch.slice(0,9).every((value,index)=>Math.abs(value-updated.modelToSketch[index])<1e-10))throw new DefinitionError('选定区域的原草图旋转已变化');
  const evidence=[];
  const driven=selections.map(selection=>{
    if(selection.kind!=='source-sketch-region'){
      if(!selection.closed||!selection.contourSegments?.length||selection.contourSegments.some(id=>!original.segments.some(segment=>segment.id===id)||!updated.segments.some(segment=>segment.id===id)))throw new DefinitionError('选定轮廓未对应原始草图段');
      evidence.push({kind:'native-contour-identities',segmentCount:selection.contourSegments.length});return structuredClone(selection);
    }
    const resolved=resolveSketchRegion(original,selection);let provenanceIndex=0;
    const loops=resolved.loops.map((loop,loopIndex)=>({outer:loop.outer,edges:loop.segments.map((segment,edgeIndex)=>{
      const provenance=resolved.provenance[provenanceIndex++];
      if(provenance.sourceSegments.length!==1)throw new DefinitionError('受驱区域边界包含多段或裁剪拓扑');
      const id=provenance.sourceSegments[0].id,source=original.segments.find(candidate=>candidate.id===id),target=updated.segments.find(candidate=>candidate.id===id),edge=selection.loops[loopIndex].edges[edgeIndex];
      if(!target||target.type!==source.type)throw new DefinitionError('受驱区域的源曲线身份已变化');
      const fullCircle=source.type===1&&near(source.geometry.start,source.geometry.end)&&edge.trimmedCurve&&Math.abs(edge.trimmedCurve.maximum-edge.trimmedCurve.minimum-2*Math.PI)<1e-10;
      if(source.type===1&&!fullCircle)throw new DefinitionError('受驱区域包含尚未求解的裁剪圆弧');
      if(source.type===0&&!((near(segment.geometry.start,source.geometry.start)&&near(segment.geometry.end,source.geometry.end))||(near(segment.geometry.start,source.geometry.end)&&near(segment.geometry.end,source.geometry.start))))throw new DefinitionError('受驱区域包含尚未求解的裁剪直线');
      const local=coordinates=>[coordinates[0],coordinates[1],resolved.planeOffset],world=coordinates=>toModel(local(coordinates),updated.modelToSketch);
      let start,end,circleParameters=null,lineParameters=null;
      if(fullCircle){start=world(target.geometry.start);end=[...start];circleParameters=[...world(target.geometry.center),...edge.circleParameters.slice(3,6),target.geometry.radius];}
      else{const forward=near(segment.geometry.start,source.geometry.start);start=world(forward?target.geometry.start:target.geometry.end);end=world(forward?target.geometry.end:target.geometry.start);const length=Math.hypot(...start.map((value,index)=>end[index]-value));if(!(length>1e-12))throw new DefinitionError('受驱区域边界已退化');lineParameters=[...start,...start.map((value,index)=>(end[index]-value)/length)];}
      return {...structuredClone(edge),start,end,circleParameters,lineParameters,trimmedCurve:fullCircle?{...edge.trimmedCurve,start,end}:{start,end,minimum:0,maximum:Math.hypot(...start.map((value,index)=>end[index]-value)),sameSense:true}};
    })}));
    evidence.push({kind:'complete-source-region-curves',loopCount:loops.length,segmentCount:resolved.provenance.length});
    const result={...structuredClone(selection),modelToSketch:[...updated.modelToSketch],loops};resolveSketchRegion(updated,result);return result;
  });
  return {selections:driven,evidence};
}
