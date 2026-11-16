import {DefinitionError} from './expressions.mjs';

const pointTolerance=1e-9; // metres; source coordinates are stored without rounding.
const near2=(left,right)=>Math.hypot(left[0]-right[0],left[1]-right[1])<=pointTolerance;
const projectPoint=(point,matrix)=>[0,1,2].map(row=>matrix[row]*point[0]+matrix[3+row]*point[1]+matrix[6+row]*point[2]+matrix[9+row]);
const projectDirection=(vector,matrix)=>[0,1,2].map(row=>matrix[row]*vector[0]+matrix[3+row]*vector[1]+matrix[6+row]*vector[2]);

// Regions contain trimmed edges, which may be only a portion of a sketch line.
// Resolve every edge against its parent sketch; never select the shorter circle arc by default.
export function resolveSketchRegion(sketch,region){
  if(region?.kind!=='source-sketch-region'||!region.loops?.length)throw new DefinitionError('选定区域没有原始边界');
  const matrix=sketch.modelToSketch;
  if(!Array.isArray(matrix)||matrix.length<13||!Array.isArray(region.modelToSketch)||!matrix.slice(0,13).every((value,index)=>Number.isFinite(value)&&Math.abs(value-region.modelToSketch[index])<1e-10)||Math.abs(matrix[12]-1)>1e-10)throw new DefinitionError('选定区域无法对应同一原草图坐标系');
  // IEdge curves use model space. Project their analytic definitions with the captured sketch transform.
  const localLoops=region.loops.map(loop=>({...loop,edges:loop.edges.map(edge=>{
    const modelStart=edge.start??edge.trimmedCurve?.start,modelEnd=edge.end??edge.trimmedCurve?.end;
    return {...edge,start:modelStart?projectPoint(modelStart,matrix):null,end:modelEnd?projectPoint(modelEnd,matrix):null,circleParameters:edge.circleParameters?[...projectPoint(edge.circleParameters.slice(0,3),matrix),...projectDirection(edge.circleParameters.slice(3,6),matrix),edge.circleParameters[6]]:null};
  })}));
  const sourceSegments=sketch.segments.filter(segment=>!segment.construction);
  if(region.loops.some(loop=>!loop.edges?.length))throw new DefinitionError('选定区域包含空边界');
  const regionPoints=localLoops.flatMap(loop=>loop.edges.flatMap(edge=>[edge.start,edge.end,...(edge.circleParameters?[edge.circleParameters.slice(0,3)]:[])]));
  if(regionPoints.some(point=>!Array.isArray(point)||point.length<3||!point.every(Number.isFinite)))throw new DefinitionError('选定区域边界端点缺失');
  const planeOffset=regionPoints[0][2];
  if(regionPoints.some(point=>Math.abs(point[2]-planeOffset)>pointTolerance))throw new DefinitionError('选定区域不在同一草图平行平面');
  const provenance=[];
  const loops=localLoops.map(loop=>{
    const segments=loop.edges.map((edge,edgeIndex)=>{
      let candidates;
      if(edge.circleParameters){
        const circle=edge.circleParameters;
        const completeCircle=edge.trimmedCurve&&Math.abs(edge.trimmedCurve.maximum-edge.trimmedCurve.minimum-2*Math.PI)<1e-10;
        if(Math.abs(circle[3])+Math.abs(circle[4])>1e-10||Math.abs(Math.abs(circle[5])-1)>1e-10)throw new DefinitionError('选定区域圆弧法向未核验');
        candidates=sourceSegments.filter(segment=>{
          const arc=segment.geometry;
          return segment.type===1&&Math.abs(arc.center[2])<=pointTolerance&&Math.abs(arc.start[2])+Math.abs(arc.end[2])<=pointTolerance&&near2(circle,arc.center)&&Math.abs(circle[6]-arc.radius)<=pointTolerance&&(completeCircle?near2(arc.start,arc.end)&&near2(edge.start,edge.end):((near2(edge.start,arc.start)&&near2(edge.end,arc.end))||(near2(edge.start,arc.end)&&near2(edge.end,arc.start))));
        });
      }else if(edge.lineParameters){
        candidates=sourceSegments.filter(segment=>{
          if(segment.type!==0)return false;
          const {start,end}=segment.geometry;
          if(Math.abs(start[2])+Math.abs(end[2])>pointTolerance)return false;
          const delta=[end[0]-start[0],end[1]-start[1]],length=Math.hypot(...delta);
          if(!(length>pointTolerance))return false;
          return [edge.start,edge.end].every(point=>{
            const offset=[point[0]-start[0],point[1]-start[1]],projection=(offset[0]*delta[0]+offset[1]*delta[1])/length;
            return Math.abs(offset[0]*delta[1]-offset[1]*delta[0])/length<=pointTolerance&&projection>=-pointTolerance&&projection<=length+pointTolerance;
          });
        });
      }else throw new DefinitionError('选定区域包含尚未支持的解析曲线');
      if(!candidates.length||(edge.circleParameters&&candidates.length!==1))throw new DefinitionError('选定区域边界无法唯一对应原草图曲线',{candidateCount:candidates.length,edgeId:edge.id});
      const source=candidates[0];
      provenance.push({edgeId:edge.id,sourceSegments:candidates.map(candidate=>({id:candidate.id,name:candidate.name})),type:source.type,proof:source.type===1?(edge.trimmedCurve&&Math.abs(edge.trimmedCurve.maximum-edge.trimmedCurve.minimum-2*Math.PI)<1e-10?'unique-complete-circle-with-2pi-trim':'unique-circle-and-both-endpoints'):'collinear-source-lines-containing-trimmed-endpoints'});
      if(source.type===1){
        if(near2(source.geometry.start,source.geometry.end))return {...source,id:edge.id??'region-edge-'+edgeIndex};
        const forward=near2(edge.start,source.geometry.start);
        return {...source,id:edge.id??'region-edge-'+edgeIndex,geometry:{...source.geometry,start:(forward?source.geometry.start:source.geometry.end),end:(forward?source.geometry.end:source.geometry.start),rotation:source.geometry.rotation*(forward?1:-1)}};
      }
      return {type:0,construction:false,name:'选区直边'+edgeIndex,id:edge.id??'region-edge-'+edgeIndex,geometry:{start:[edge.start[0],edge.start[1],0],end:[edge.end[0],edge.end[1],0]}};
    });
    return {outer:loop.outer,segments};
  });
  return {loops,planeOffset,provenance};
}
