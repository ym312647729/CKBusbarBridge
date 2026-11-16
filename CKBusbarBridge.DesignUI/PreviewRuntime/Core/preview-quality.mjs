import {evaluateTemplate} from './geometry.mjs';
import {curveQualityLevels} from './curve-quality.mjs';

export const minimumComplexSegments=12;
export function complexQualityLevels(initial){
  if(!Number.isInteger(initial)||initial<minimumComplexSegments||initial>720)throw new Error('曲面分段数必须为12至720之间的整数');
  return [...new Set([initial,Math.max(minimumComplexSegments,Math.floor(initial*2/3)),minimumComplexSegments])].sort((left,right)=>right-left);
}
export function previewSettingsSignature(settings){
  return JSON.stringify([settings.arcSegments??24,settings.complexArcSegments??settings.arcSegments??24,settings.chordToleranceMm??.3,settings.objectTriangleBudget??80000]);
}
function topologySignature(result){
  const components=[];
  for(const solid of result.solids){
    const bodies=solid.decompose();
    try{for(const body of bodies)components.push(body.genus());}finally{bodies.forEach(body=>body.delete());}
  }
  return JSON.stringify(components.sort((left,right)=>left-right));
}

// Regular holes retain their circular resolution. Complex profiles and revolutions share an error budget.
export async function evaluatePreview(kernel,template,configuration,overrides,settings,loadTemplate){
  const levels=curveQualityLevels(settings),budget=settings.objectTriangleBudget??80000;
  let retained,retainedLevel,retainedTriangles,baselineTopology;const attempts=[],geometryTimings={cutFeatureMs:0,otherFeatureMs:0};let refinementError=null;
  try{
    for(const level of levels){
      let candidate;
      try{candidate=await evaluateTemplate(kernel,template,configuration,overrides,{...settings,complexArcSegments:level.segments,chordToleranceMm:level.chordToleranceMm},loadTemplate);}
      catch(error){if(!retained)throw error;refinementError=error.message;break;}
      for(const key of Object.keys(geometryTimings))geometryTimings[key]+=candidate.geometryTimings?.[key]??0;
      let triangleCount,topology;
      try{triangleCount=candidate.solids.reduce((sum,solid)=>sum+solid.numTri(),0);topology=topologySignature(candidate);}
      catch(error){candidate.solids.forEach(solid=>solid.delete());throw error;}
      if(retained&&(topology!==baselineTopology||triangleCount>=retainedTriangles)){
        candidate.solids.forEach(solid=>solid.delete());
        refinementError=topology!==baselineTopology?'降低曲面精度会改变实体连通或孔拓扑，已保留上一合法精度':'降低曲面精度未减少面数，已保留上一合法精度';
        break;
      }
      retained?.solids.forEach(solid=>solid.delete());retained=candidate;retainedLevel=level;retainedTriangles=triangleCount;baselineTopology??=topology;
      attempts.push({complexArcSegments:level.segments,chordToleranceMm:level.chordToleranceMm,triangleCount});
      if(triangleCount<=budget||!candidate.complexFeatureCount)break;
    }
    const overBudget=retainedTriangles>budget,coarsened=retainedLevel!==levels[0];
    return {...retained,geometryTimings,preview:{complexFeatureCount:retained.complexFeatureCount??0,complexArcSegments:retainedLevel.segments,chordToleranceMm:retainedLevel.chordToleranceMm,requestedChordToleranceMm:levels[0].chordToleranceMm,curveQuality:retained.curveQuality??[],regularArcSegments:settings.arcSegments??24,triangleCount:retainedTriangles,canSimplify:!!retained.complexFeatureCount&&retainedLevel!==levels.at(-1)&&!refinementError,coarsened,topologyPreserved:true,topologySignature:baselineTopology,attempts,refinementError},previewDisplayMode:overBudget?'outline':'solid',qualityWarning:overBudget?'已到可用曲面精度，超出对象预算的局部以轮廓显示':refinementError??(coarsened?'复杂曲面预览弦差已按预算放宽至'+retainedLevel.chordToleranceMm.toFixed(2)+'，规则圆孔保持原精度':null)};
  }catch(error){retained?.solids.forEach(solid=>solid.delete());throw error;}
}
