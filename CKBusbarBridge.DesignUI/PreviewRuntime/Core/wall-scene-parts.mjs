import {DefinitionError} from './expressions.mjs';
import {axisSketchDrive} from './axis-sketch-drive.mjs';
import {pairedExtrudedTopology,pairedThroughCutPlanes,mapExtrudedPlane} from './extruded-topology.mjs';
import {sketchSupportPlane,movedSketchMatrix,mapSketchReference} from './sketch-dependencies.mjs';

const canonical=name=>name.split('@').slice(0,-1).join('@');
const contracts={
  '墙':['D2@草图1','开孔高度@草图2','开孔宽度@草图2','D1@草图2'],
  '梁':['D2@草图1'],
  '穿墙套管安装板':['安装板长@草图1','安装板宽@草图1']
};

export function deriveWallScenePart(sourceTemplate,configurationName,dimensions){
  const match=/^模板\/面对墙模板\(柜[前后]向墙\)\/柜体场景示意\/(墙|梁|穿墙套管安装板)\.SLDPRT$/.exec(sourceTemplate.source.path),keys=match&&contracts[match[1]];
  if(!keys||!dimensions||Object.keys(dimensions).length!==keys.length||keys.some(key=>!Number.isFinite(dimensions[key])||dimensions[key]<=0))throw new DefinitionError('墙体场景尺寸未对应原业务零件与驱动尺寸');
  const original=sourceTemplate.configurations.find(configuration=>configuration.name===configurationName);
  if(!original||original.components.length||original.equations.length)throw new DefinitionError('墙体场景驱动源不是预期的原零件配置');
  const template=structuredClone(sourceTemplate),configuration=template.configurations.find(row=>row.name===configurationName),topologies=[],evidence=[],consumed=new Set();delete configuration.validation;
  for(const source of original.features){
    const target=configuration.features.find(feature=>feature.id===source.id),parents=topologies.filter(topology=>source.parents.includes(topology.feature));
    if(source.sketch){
      const local=Object.fromEntries(Object.entries(dimensions).filter(([name])=>name.split('@').at(-1)===source.name)),drive=axisSketchDrive(original,source.name,{governingDimensions:Object.keys(local)});
      const support=parents.length?mapExtrudedPlane(sketchSupportPlane(source.sketch),parents):{delta:[0,0,0],proof:[]};
      if(!parents.length&&!source.parents.some(name=>original.features.find(feature=>feature.name===name&&feature.type==='RefPlane'&&feature.definition?.properties.Type===10)))throw new DefinitionError('墙体场景草图没有已解析的原支撑面');
      const matrix=movedSketchMatrix(source.sketch,support.delta),references=Object.fromEntries(drive.references.map(reference=>[reference.id,mapSketchReference(reference,source.sketch,matrix,parents,source.parents.includes('原点'))])),solved=drive.solve(local,references);solved.sketch.modelToSketch=matrix;target.sketch=solved.sketch;
      Object.keys(local).forEach(name=>consumed.add(name));evidence.push({feature:source.name,dimensions:local,equationCount:solved.equationCount,referenceCount:solved.referenceCount,supportProof:support.proof});
    }
    if(['Extrusion','Boss'].includes(source.type))topologies.push(pairedExtrudedTopology(original,configuration,source.name));
    else if(source.type==='Cut')topologies.push(pairedThroughCutPlanes(original,configuration,source.name));
  }
  if(keys.some(name=>!consumed.has(name)))throw new DefinitionError('墙体场景驱动尺寸未全部进入原草图约束');
  for(const feature of configuration.features)for(const dimension of feature.dimensions??[])if(Object.hasOwn(dimensions,canonical(dimension.name)))dimension.value=dimensions[canonical(dimension.name)]/1000;
  return {template,topologies,evidence,dimensions,source:'original-wall-scene-profile-equations-and-native-incidences',nativeGeometryEquivalenceVerified:false};
}
