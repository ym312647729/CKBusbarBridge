import {DefinitionError} from './expressions.mjs';
import {axisSketchDrive} from './axis-sketch-drive.mjs';
import {driveCompleteSketchRegions} from './driven-sketch-regions.mjs';
import {originalCabinetPartName} from './cabinet-openings.mjs';
import {extrudedTopology,pairedExtrudedTopology,mapExtrudedPlane} from './extruded-topology.mjs';
import {sketchSupportPlane,movedSketchMatrix,mapSketchReference} from './sketch-dependencies.mjs';

const canonical=name=>name.split('@').slice(0,-1).join('@');

export function cabinetNominalBounds(configuration){
  // Only the driven carcass sets the dimensions. The old raised front feature
  // reaches into the base's envelope and is only used to solve captured mates.
  const bases=configuration.features.filter(feature=>feature.type==='Extrusion'&&feature.parents.includes('草图1'));
  if(bases.length!==1)throw new DefinitionError('柜体名义外形缺少唯一基础拉伸');
  const points=extrudedTopology(configuration,bases[0]).vertices.map(({point})=>point.map(value=>value*1000));
  return {min:[0,1,2].map(axis=>Math.min(...points.map(point=>point[axis]))),
    max:[0,1,2].map(axis=>Math.max(...points.map(point=>point[axis])))};
}

export function deriveCabinetBody(sourceTemplate,configurationName,specification){
  if(!originalCabinetPartName(sourceTemplate.source.path))throw new DefinitionError('柜体实体驱动未对应本项目原业务模板');
  if(!specification||Object.keys(specification).length!==3||!['height','depth','width'].every(key=>Number.isFinite(specification[key])&&specification[key]>0))throw new DefinitionError('柜体长宽高必须是三个正有限数');
  const original=sourceTemplate.configurations.find(row=>row.name===configurationName);
  if(!original||original.components.length)throw new DefinitionError('柜体实体驱动源配置不是零件');
  const baseProfiles=original.features.filter(feature=>feature.name==='草图1'&&feature.sketch),bases=original.features.filter(feature=>feature.type==='Extrusion'&&feature.parents.includes('草图1'));
  if(baseProfiles.length!==1||bases.length!==1)throw new DefinitionError('柜体没有唯一原基础拉伸');
  const dimension=(feature,token)=>{const names=[...new Set(feature.dimensions.filter(row=>row.name.includes(token)).map(row=>canonical(row.name)))];if(names.length!==1)throw new DefinitionError('柜体业务尺寸未对应唯一原尺寸：'+token);return names[0];};
  const names={depth:dimension(baseProfiles[0],'柜深度@草图1'),height:dimension(baseProfiles[0],'柜高度@草图1'),width:dimension(bases[0],'柜宽度@'+bases[0].name)},values=Object.fromEntries(Object.entries(names).map(([key,name])=>[name,specification[key]])),globals={};
  for(const [key,name]of Object.entries(names)){const assignments=original.equations.filter(equation=>!equation.disabled).map(equation=>/^\s*"([^"]+)"\s*=\s*"([^"]+)"\s*$/.exec(equation.expression)).filter(match=>match?.[1]===name);if(assignments.length!==1)throw new DefinitionError('柜体原全局变量方程未唯一对应：'+name);globals[assignments[0][2]]=specification[key];}
  const template=structuredClone(sourceTemplate),configuration=template.configurations.find(row=>row.name===configurationName),evidence=[],topologies=[];delete configuration.validation;
  for(const equation of configuration.equations){const global=/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1];if(equation.globalVariable&&Object.hasOwn(globals,global))equation.expression='"'+global+'" = '+globals[global];}
  const baseProfile=configuration.features.find(feature=>feature.id===baseProfiles[0].id),base=configuration.features.find(feature=>feature.id===bases[0].id),solvedBase=axisSketchDrive(original,baseProfile.name,{governingDimensions:[names.depth,names.height]}).solve({[names.depth]:specification.depth,[names.height]:specification.height});
  baseProfile.sketch=solvedBase.sketch;base.definition.properties.forwardDepth=specification.width/1000;
  for(const feature of configuration.features)for(const captured of feature.dimensions??[])if(Object.hasOwn(values,canonical(captured.name)))captured.value=values[canonical(captured.name)]/1000;
  for(const relation of baseProfile.sketch.relations)if(relation.dimension&&Object.hasOwn(values,canonical(relation.dimension.name)))relation.dimension.value=values[canonical(relation.dimension.name)]/1000;
  for(const source of original.features){const target=configuration.features.find(feature=>feature.id===source.id),parents=topologies.filter(topology=>source.parents.includes(topology.feature));
    if(source.sketch&&source.id!==baseProfile.id){
      const supported=mapExtrudedPlane(sketchSupportPlane(source.sketch),parents),matrix=movedSketchMatrix(source.sketch,supported.delta),drive=axisSketchDrive(original,source.name),references=Object.fromEntries(drive.references.map(reference=>[reference.id,mapSketchReference(reference,source.sketch,matrix,parents,source.parents.includes('原点'))])),solved=drive.solve({},references);
      solved.sketch.modelToSketch=matrix;target.sketch=solved.sketch;
      evidence.push({feature:source.name,equationCount:solved.equationCount,freeConstructionCoordinates:solved.freeConstructionCoordinates,referenceCount:drive.references.length,supportProof:supported.proof});
    }
    const end=source.definition?.properties.forwardEndReference;
    if(end?.planeParameters)target.definition.properties.forwardEndReference=mapExtrudedPlane(end,parents).reference;
    if(source.type==='RefPlane'&&parents.length)for(const [index,constraint]of (source.definition?.properties.constraintReferences??[]).entries())if(constraint.reference?.planeParameters)target.definition.properties.constraintReferences[index].reference=mapExtrudedPlane(constraint.reference,parents).reference;
    if(['Extrusion','Boss'].includes(source.type))topologies.push(pairedExtrudedTopology(original,configuration,source.name));
  }
  for(const source of original.features.filter(feature=>feature.type==='Cut')){const target=configuration.features.find(feature=>feature.id===source.id),profiles=original.features.filter(feature=>feature.sketch&&source.parents.includes(feature.name));if(profiles.length!==1)throw new DefinitionError('柜体切口没有唯一原草图');const updated=configuration.features.find(feature=>feature.id===profiles[0].id),selection=driveCompleteSketchRegions(profiles[0].sketch,updated.sketch,source.definition.properties.Contours);target.definition.properties.Contours=selection.selections;}
  return {template,evidence,topologies,values,globals,source:'original-cabinet-equations-and-extrusion-incidences',nativeGeometryEquivalenceVerified:false};
}
