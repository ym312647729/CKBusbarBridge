import {DefinitionError} from './expressions.mjs';
import {mapExtrudedPlane,samePoint} from './extruded-topology.mjs';
import {swMatrix,inverseRigidMatrix} from './geometry.mjs';

const project=(point,matrix)=>[0,1,2].map(axis=>point[0]*matrix[axis]+point[1]*matrix[3+axis]+point[2]*matrix[6+axis]+matrix[9+axis]);
const near2=(first,second)=>Math.hypot(first[0]-second[0],first[1]-second[1])<1e-8;

export function sketchSupportPlane(sketch){const matrix=inverseRigidMatrix(swMatrix(sketch.modelToSketch,1));return {planeParameters:[...matrix.slice(8,11),...matrix.slice(12,15)]};}
export function movedSketchMatrix(sketch,delta){const result=[...sketch.modelToSketch];for(let axis=0;axis<3;axis++)result[9+axis]-=delta[0]*result[axis]+delta[1]*result[3+axis]+delta[2]*result[6+axis];return result;}
export function mapSketchReference(reference,originalSketch,updatedMatrix,topologies,hasOrigin){
  if(reference.start&&reference.end){
    const matches=topologies.flatMap(topology=>topology.edges).flatMap(pair=>samePoint(reference.start,pair.source.start)&&samePoint(reference.end,pair.source.end)?[{start:pair.target.start,end:pair.target.end}]:samePoint(reference.start,pair.source.end)&&samePoint(reference.end,pair.source.start)?[{start:pair.target.end,end:pair.target.start}]:[]);
    if(!matches.length||matches.some(pair=>!samePoint(pair.start,matches[0].start)||!samePoint(pair.end,matches[0].end)))throw new DefinitionError('外部草图边未对应唯一原拉伸边界');
    return {...reference,...matches[0]};
  }
  if(reference.referencePlaneTransform)return mapExtrudedPlane(reference,topologies).reference;
  if(reference.positionEvidence==='native-coincidence-with-captured-sketch-point'){
    const candidates=topologies.flatMap(topology=>topology.vertices).filter(pair=>near2(project(pair.source.point,originalSketch.modelToSketch),reference.point)).map(pair=>project(pair.target.point,updatedMatrix));
    if(!candidates.length||candidates.some(point=>!near2(point,candidates[0])))throw new DefinitionError('外部重合点未对应可证明等价的原拉伸顶点');
    return {...reference,point:[candidates[0][0],candidates[0][1],0]};
  }
  if(hasOrigin&&reference.point?.every(value=>Math.abs(value)<1e-12))return {...reference,point:[0,0,0]};
  throw new DefinitionError('外部草图定位点缺少可证明的原拓扑归属');
}
