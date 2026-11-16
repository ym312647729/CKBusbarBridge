import {DefinitionError} from './expressions.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {cabinetHoleModelContract} from './cabinet-openings.mjs';
const near=(first,second)=>Math.hypot(...first.map((value,axis)=>value-second[axis]))<1e-5;

// Root mounting coordinates reference a point on a named cabinet component.
// Assembly-context persistent IDs differ from part IDs, so first prove the
// stored local point and its exact old world incidence within that parent.
// Follow the same part feature/point identity after its sketch is driven.
export function driveCabinetMountCoordinates(source,updated,bodies){
  const solved=[],pending=[];
  for(const coordinate of source.features.filter(feature=>feature.type==='CoordSys')){
    const body=bodies.find(body=>coordinate.parents.includes(body.rootComponent.name));
    if(!body){pending.push(coordinate);continue;}
    const properties=coordinate.definition?.properties,point=properties?.OriginEntity?.point,transform=properties?.Transform?.transform;
    if(!point||!transform)throw new DefinitionError('柜体安装坐标缺少原点定义：'+coordinate.name);
    const oldPlacement=multiplyMatrix(swMatrix(body.rootComponent.transform),swMatrix(body.originalBodyTransform)),updatedComponent=updated.components.find(component=>component.id===body.rootComponent.id),newPlacement=multiplyMatrix(swMatrix(updatedComponent.transform),swMatrix(body.updatedBodyTransform)),world=swMatrix(transform).slice(12,15);
    const sketchName=cabinetHoleModelContract(/柜-?前/.test(coordinate.name),coordinate.name.includes('左右开孔')).sketchName;
    const candidates=body.original.features.filter(feature=>feature.sketch&&feature.name===sketchName).flatMap(feature=>feature.sketch.points.filter(candidate=>near(candidate.coordinates,point)).map(candidate=>({feature,candidate}))).filter(({feature,candidate})=>near(transformPoint(transformPoint(candidate.coordinates.map(value=>value*1000),inverseRigidMatrix(swMatrix(feature.sketch.modelToSketch))),oldPlacement),world));
    if(candidates.length!==1)throw new DefinitionError('柜体安装原点未唯一对应所属柜体草图点：'+coordinate.name);
    const {feature,candidate}=candidates[0],nextFeature=body.derived.features.find(row=>row.id===feature.id),nextPoint=nextFeature?.sketch.points.find(row=>row.id===candidate.id);
    if(!nextPoint)throw new DefinitionError('柜体安装草图点身份改变：'+coordinate.name);
    const next=transformPoint(transformPoint(nextPoint.coordinates.map(value=>value*1000),inverseRigidMatrix(swMatrix(nextFeature.sketch.modelToSketch))),newPlacement),target=updated.features.find(row=>row.id===coordinate.id),matrix=[...transform];
    for(let axis=0;axis<3;axis++)matrix[9+axis]=next[axis]/1000;
    target.definition.properties.Transform.transform=matrix;target.offlineCoordinateSolution={source:'driven-cabinet-sketch-point-and-native-mates',originMm:next,rotationPreserved:true,sourceGuide:feature.name};solved.push(coordinate.name);
  }
  updated.features=updated.features.filter(feature=>!pending.some(coordinate=>coordinate.id===feature.id));
  return {solved,pending:pending.map(feature=>feature.name)};
}
