import {DefinitionError} from './expressions.mjs';
const close=(left,right)=>left.length===right.length&&left.every((value,index)=>Math.abs(value-right[index])<1e-7);
const canonical=name=>name.split('@').slice(0,-1).join('@');

export function solveLinearSystem(equations,variableCount){
  const rows=equations.map(row=>[...row]);let pivotRow=0;const pivots=[];
  for(let column=0;column<variableCount&&pivotRow<rows.length;column++){
    let selected=pivotRow;for(let index=pivotRow+1;index<rows.length;index++)if(Math.abs(rows[index][column])>Math.abs(rows[selected][column]))selected=index;
    if(Math.abs(rows[selected][column])<1e-9)continue;
    [rows[selected],rows[pivotRow]]=[rows[pivotRow],rows[selected]];const divisor=rows[pivotRow][column];rows[pivotRow]=rows[pivotRow].map(value=>value/divisor);
    for(let index=0;index<rows.length;index++)if(index!==pivotRow){const factor=rows[index][column];if(Math.abs(factor)>1e-12)for(let offset=column;offset<=variableCount;offset++)rows[index][offset]-=factor*rows[pivotRow][offset];}
    pivots.push(column);pivotRow++;
  }
  for(const row of rows)if(row.slice(0,variableCount).every(value=>Math.abs(value)<1e-8)&&Math.abs(row[variableCount])>1e-5)throw new DefinitionError('草图线性约束冲突');
  if(pivots.length!==variableCount)throw new DefinitionError('草图线性约束不能唯一确定全部点');
  const values=Array(variableCount).fill(0);for(let index=0;index<pivots.length;index++)values[pivots[index]]=rows[index][variableCount];return values;
}

// Conservatively recognize a complete linear system; all active relations must be understood.
export function linearSketchDrive(configuration,baseFeature,options={}){
  try{
    if(configuration.components.length||configuration.equations.some(equation=>!equation.disabled&&equation.userDisposition?.mode!=='void-by-user')||baseFeature.definition?.properties.Contours?.length)return null;
    const profiles=configuration.features.filter(feature=>feature.sketch&&baseFeature.parents.includes(feature.name)&&(!options.profileName||feature.name===options.profileName));
    if(profiles.length!==1)return null;
    const profile=profiles[0],sketch=profile.sketch,points=new Map();
    if(!sketch.points?.length||sketch.points.length>64||sketch.segments.some(segment=>segment.type!==0))return null;
    for(const point of sketch.points){if(!point.id||points.has(point.id)||Math.abs(point.coordinates[2])>1e-7)return null;points.set(point.id,{coordinates:point.coordinates,external:false,index:points.size});}
    const linePoints=new Map();
    for(const segment of sketch.segments){
      const endpoints=[segment.geometry.start,segment.geometry.end].map(endpoint=>{
        const matching=[...points.entries()].filter(([,point])=>!point.external&&close(point.coordinates,endpoint));if(matching.length!==1)throw new DefinitionError('草图端点没有唯一原始点');return matching[0][0];
      });
      if(endpoints[0]===endpoints[1])return null;linePoints.set(segment.id,{endpoints,segment});
    }
    const relations=sketch.relations.filter(relation=>!relation.suppressed),lineDirections=new Map();
    for(const relation of relations){
      if(![1,4,5,9,12,17].includes(relation.type))return null;
      for(const entity of relation.entities){
        if(!entity?.id)return null;
        if(entity.point&&!points.has(entity.id)){if(Math.abs(entity.point[2])>1e-7)return null;points.set(entity.id,{coordinates:entity.point,external:true,index:points.size});}
        if(entity.point&&!close(points.get(entity.id).coordinates,entity.point))return null;
      }
      if([4,5].includes(relation.type)){
        if(relation.entities.length!==1||!linePoints.has(relation.entities[0].id))return null;
        const identity=relation.entities[0].id,direction=relation.type===4?0:1,prior=lineDirections.get(identity);
        if(prior!==undefined&&prior!==direction)return null;lineDirections.set(identity,direction);
      }
    }
    const variableCount=points.size*2,equations=[],dimensions=[];
    const add=(terms,value)=>{const row=Array(variableCount+1).fill(0);for(const [identity,axis,coefficient]of terms){const point=points.get(identity);if(!point)throw new DefinitionError('草图关系缺少原始点');row[point.index*2+axis]+=coefficient;}row[variableCount]=value;equations.push(row);};
    const position=(identity,axis)=>points.get(identity).coordinates[axis]*1000;
    const referenceRows=[];
    for(const [identity,point]of points)if(point.external)for(let axis=0;axis<2;axis++){referenceRows.push({identity,axis,row:equations.length});add([[identity,axis,1]],position(identity,axis));}
    for(const relation of relations){
      const pointEntities=relation.entities.filter(entity=>entity.point),lineEntities=relation.entities.filter(entity=>linePoints.has(entity.id));
      if([4,5].includes(relation.type)){
        const [first,second]=linePoints.get(lineEntities[0].id).endpoints,axis=relation.type===4?1:0;
        if(Math.abs(position(first,axis)-position(second,axis))>1e-5)return null;
        add([[first,axis,1],[second,axis,-1]],0);
      }else if(relation.type===9){
        if(pointEntities.length===2&&relation.entities.length===2)for(let axis=0;axis<2;axis++)add([[pointEntities[0].id,axis,1],[pointEntities[1].id,axis,-1]],0);
        else if(pointEntities.length===1&&lineEntities.length===1&&relation.entities.length===2){
          const direction=lineDirections.get(lineEntities[0].id);if(direction===undefined)return null;const axis=1-direction,endpoint=linePoints.get(lineEntities[0].id).endpoints[0];
          add([[pointEntities[0].id,axis,1],[endpoint,axis,-1]],0);
        }else return null;
      }else if(relation.type===12){
        if(pointEntities.length!==1||lineEntities.length!==1||relation.entities.length!==2)return null;
        const [first,second]=linePoints.get(lineEntities[0].id).endpoints;
        for(let axis=0;axis<2;axis++)add([[pointEntities[0].id,axis,1],[first,axis,-.5],[second,axis,-.5]],0);
      }else if(relation.type===17){
        if(pointEntities.length===1&&relation.entities.length===1)for(let axis=0;axis<2;axis++)add([[pointEntities[0].id,axis,1]],position(pointEntities[0].id,axis));
        else return null;
      }else{
        if(!relation.dimension||pointEntities.length!==2||relation.entities.length!==2||![1,2].includes(relation.dimension.drivenState))return null;
        const [first,second]=pointEntities.map(entity=>entity.id),direct=[...linePoints.entries()].filter(([identity,line])=>lineDirections.has(identity)&&line.endpoints.includes(first)&&line.endpoints.includes(second));
        if(direct.length!==1)return null;
        const axis=lineDirections.get(direct[0][0]),sign=Math.sign(position(second,axis)-position(first,axis));if(!sign)return null;
        const dimensionName=canonical(relation.dimension.name),sourceDimensions=configuration.features.flatMap(feature=>feature.dimensions).filter(dimension=>canonical(dimension.name)===dimensionName);
        if(!sourceDimensions.length||sourceDimensions.some(dimension=>dimension.drivenState!==relation.dimension.drivenState))return null;
        const editable=relation.dimension.drivenState===2&&sourceDimensions.every(dimension=>dimension.readOnly===false);
        const governed=options.governingDimensions?.includes(dimensionName)===true;
        dimensions.push({name:dimensionName,first,second,axis,sign,value:relation.dimension.value*1000,editable,governed});
        if(relation.dimension.drivenState===2||governed)add([[second,axis,sign],[first,axis,-sign]],relation.dimension.value*1000);
      }
    }
    function solve(overrides={},referencePoints={}){
      const mutable=equations.map(row=>[...row]);
      for(const [identity,coordinates]of Object.entries(referencePoints)){
        if(!points.get(identity)?.external||!Array.isArray(coordinates)||coordinates.length!==3||!coordinates.every(Number.isFinite)||Math.abs(coordinates[2])>1e-7)throw new DefinitionError('外部草图点没有可验证的二维引用');
        for(const reference of referenceRows.filter(row=>row.identity===identity))mutable[reference.row][variableCount]=coordinates[reference.axis]*1000;
      }
      for(const dimension of dimensions.filter(dimension=>(dimension.editable||dimension.governed)&&Object.hasOwn(overrides,dimension.name))){
        if(!Number.isFinite(overrides[dimension.name])||overrides[dimension.name]<=0)throw new DefinitionError('受驱草图长度必须为有限正数');
        const row=Array(variableCount+1).fill(0);row[points.get(dimension.second).index*2+dimension.axis]=dimension.sign;row[points.get(dimension.first).index*2+dimension.axis]=-dimension.sign;
        const matching=mutable.filter(candidate=>candidate.slice(0,-1).every((value,index)=>value===row[index]));if(matching.length!==1)throw new DefinitionError('草图尺寸约束不能唯一关联');
        matching[0][variableCount]=overrides[dimension.name];
      }
      const values=solveLinearSystem(mutable,variableCount),updated=structuredClone(sketch),solvedPoint=identity=>[values[points.get(identity).index*2]/1000,values[points.get(identity).index*2+1]/1000,0];
      for(const point of updated.points)point.coordinates=solvedPoint(point.id);
      for(const segment of updated.segments){const endpoints=linePoints.get(segment.id).endpoints;segment.geometry.start=solvedPoint(endpoints[0]);segment.geometry.end=solvedPoint(endpoints[1]);}
      const measured={},dimensionEdges={};for(const dimension of dimensions){const first=solvedPoint(dimension.first),second=solvedPoint(dimension.second);measured[dimension.name]=Math.hypot(...first.map((value,index)=>value-second[index]))*1000;dimensionEdges[dimension.name]=[first,second].map(point=>point.map(value=>value*1000));}
      return {sketch:updated,values:measured,dimensionEdges};
    }
    const baseline=solve();for(const point of sketch.points)if(!close(point.coordinates,baseline.sketch.points.find(candidate=>candidate.id===point.id).coordinates))return null;
    if(!dimensions.some(dimension=>dimension.editable||dimension.governed))return null;
    return {profileName:profile.name,dimensions,solve};
  }catch{return null;}
}
