import {DefinitionError} from './expressions.mjs';
import {unionProfileSections} from './profile-section.mjs';
import {circleSegmentsForTolerance,circleChordError} from './curve-quality.mjs';

// Full turns have no start-angle ambiguity. Partial/thin/scope variants remain explicit failures.
export function revolveFeature(kernel,feature,sketch,groups,sketchToModel,arcSegments,chordToleranceMm=.3,qualityTrace=[]){
  const properties=feature.definition.properties;
  if(properties.isThin)throw new DefinitionError('薄壁旋转尚未支持：'+feature.name);
  if(![3,4].includes(properties.Type)||Math.abs(properties.forwardAngle-2*Math.PI)>1e-10||Math.abs(properties.reverseAngle)>1e-10)throw new DefinitionError('非完整一周旋转尚未支持：'+feature.name);
  if(properties.axisType!==10||!properties.Axis?.segment)throw new DefinitionError('旋转轴没有可核验的草图直线：'+feature.name);
  const axes=sketch.segments.filter(segment=>segment.type===0&&segment.name===properties.Axis.segment&&(!properties.Axis.id||segment.id===properties.Axis.id));
  if(axes.length!==1)throw new DefinitionError('旋转轴无法唯一对应原草图：'+feature.name);
  const origin=axes[0].geometry.start.map(value=>value*1000),end=axes[0].geometry.end.map(value=>value*1000);
  const delta=end.map((value,index)=>value-origin[index]),length=Math.hypot(...delta);
  if(!(length>0)||Math.abs(delta[2])>1e-7||Math.abs(origin[2])>1e-7)throw new DefinitionError('旋转轴不在草图平面：'+feature.name);
  const axis=[delta[0]/length,delta[1]/length],radial=[axis[1],-axis[0]];
  const coordinates=groups.map(group=>group.map(loop=>loop.map(point=>{const offset=[point[0]-origin[0],point[1]-origin[1]];return [offset[0]*radial[0]+offset[1]*radial[1],offset[0]*axis[0]+offset[1]*axis[1]];})));
  const radii=coordinates.flat(2).map(point=>point[0]);
  if(Math.min(...radii)<-1e-6){
    if(Math.max(...radii)>1e-6)throw new DefinitionError('旋转截面跨越轴线，尚未完成区域分割：'+feature.name);
    radial[0]*=-1;radial[1]*=-1;for(const group of coordinates)for(const loop of group)for(const point of loop)point[0]*=-1;
  }
  const section=unionProfileSections(kernel,coordinates);
  try{
    // Reserve half the requested error for profile arcs, half for revolution sampling.
    const maximumRadius=Math.max(...coordinates.flat(2).map(point=>point[0]))+chordToleranceMm/2;
    const angularSegments=circleSegmentsForTolerance(maximumRadius,chordToleranceMm/2,arcSegments);
    const angularChordErrorMm=circleChordError(maximumRadius,angularSegments);
    qualityTrace.push({feature:feature.name,kind:'revolution',angularSegments,maximumRadiusBoundMm:maximumRadius,angularChordErrorMm,profileChordToleranceMm:chordToleranceMm/2,surfaceErrorBoundMm:angularChordErrorMm+chordToleranceMm/2,chordToleranceMm});
    const revolved=section.revolve(angularSegments,360);
    try{
      // Kernel maps section Y to output Z; map it back to the selected source axis.
      const localColumns=[[radial[0],radial[1],0],[0,0,axis[0]*radial[1]-axis[1]*radial[0]],[axis[0],axis[1],0]];
      const matrix=[];
      for(const column of localColumns)matrix.push(...[0,1,2].map(row=>column.reduce((sum,value,index)=>sum+value*sketchToModel[index*4+row],0)),0);
      matrix.push(...[0,1,2].map(row=>sketchToModel[row]*origin[0]+sketchToModel[4+row]*origin[1]+sketchToModel[8+row]*origin[2]+sketchToModel[12+row]),1);
      return revolved.transform(matrix);
    }finally{revolved.delete();}
  }finally{section.delete();}
}
