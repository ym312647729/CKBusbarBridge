import {DefinitionError} from './expressions.mjs';
import {axisSketchDrive} from './axis-sketch-drive.mjs';
import {pairedExtrudedTopology,mapExtrudedPlane} from './extruded-topology.mjs';
import {sketchSupportPlane,movedSketchMatrix,mapSketchReference} from './sketch-dependencies.mjs';
import {swMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';

const canonical=dimension=>dimension.name.split('@').slice(0,-1).join('@');
const near=(first,second)=>first.length===second.length&&Math.hypot(...first.map((value,index)=>value-second[index]))<1e-8;
const one=(items,label)=>{if(items.length!==1)throw new DefinitionError(label+'缺失或不唯一');return items[0];};

function circularCaps(configuration,featureName){
  const feature=one(configuration.features.filter(feature=>feature.name===featureName),'套管圆柱特征'),properties=feature.definition?.properties,profile=one(configuration.features.filter(candidate=>candidate.sketch&&feature.parents.includes(candidate.name)),'套管圆柱草图'),circle=one(profile.sketch.segments.filter(segment=>!segment.construction),'套管原圆');
  if(circle.type!==1||!near(circle.geometry.start,circle.geometry.end)||properties.FromType!==0||properties.forwardEndCondition!==0||properties.ReverseDirection||properties.isThin||!(properties.forwardDepth>0))throw new DefinitionError('套管圆柱不是原单圆直拉伸');
  const matrix=inverseRigidMatrix(swMatrix(profile.sketch.modelToSketch,1)),normal=matrix.slice(8,11),start=transformPoint(circle.geometry.center,matrix),end=start.map((value,axis)=>value+normal[axis]*properties.forwardDepth);
  return {feature:feature.name,profile:profile.name,circle,start,end,normal};
}

function pairCircularCaps(original,updated,featureName){
  const source=circularCaps(original,featureName),target=circularCaps(updated,featureName);
  return {feature:featureName,edges:[],vertices:[],faces:['start','end'].map(key=>({feature:featureName,source:{key,origin:source[key],normal:source.normal},target:{key,origin:target[key],normal:target.normal}}))};
}

// Preserve native circle, terminal, hole and body-selection identities. The
// generated geometry remains a preview and is never used as a SW model check.
export function deriveWallBushing(sourceTemplate,configurationName,model){
  if(!/^模板\/面对墙模板\(柜[前后]向墙\)\/柜体场景示意\/穿墙套管\.SLDPRT$/.test(sourceTemplate.source.path)||model.templatePath!==sourceTemplate.source.path||model.configuration!==configurationName||model.sourceSha256!==sourceTemplate.source.sha256)throw new DefinitionError('套管预览未对应原业务定义');
  const original=one(sourceTemplate.configurations.filter(configuration=>configuration.name===configurationName),'套管源配置'),template=structuredClone(sourceTemplate),configuration=template.configurations.find(configuration=>configuration.name===configurationName),dimensions=Object.fromEntries([...model.dimensions,...model.externalDimensions].map(dimension=>[dimension.dimension,dimension.valueMm])),topologies=[],evidence=[];
  delete configuration.validation;
  for(const feature of configuration.features){
    for(const dimension of feature.dimensions??[])if(Object.hasOwn(dimensions,canonical(dimension)))dimension.value=dimensions[canonical(dimension)]/1000;
    if(['Extrusion','Boss'].includes(feature.type)){
      const depth=Object.entries(dimensions).filter(([name])=>name.split('@').at(-1)===feature.name);if(depth.length>1)throw new DefinitionError('套管拉伸有多条长度驱动');if(depth.length){if(!(depth[0][1]>0))throw new DefinitionError('套管拉伸长度必须为正');feature.definition.properties.forwardDepth=depth[0][1]/1000;}
    }
  }
  for(const sourceFeature of original.features){
    const target=configuration.features.find(feature=>feature.id===sourceFeature.id);
    if(sourceFeature.sketch){
      if(sourceFeature.name==='草图4'){
        const profile=one(original.features.filter(feature=>feature.name==='草图2'),'套管主圆草图'),sourceCaps=circularCaps(original,'凸台-拉伸2'),targetCaps=circularCaps(configuration,'凸台-拉伸2'),externalCircles=sourceFeature.sketch.relations.filter(relation=>relation.type===14).flatMap(relation=>relation.entities.filter(entity=>entity.circleParameters));
        const external=one(externalCircles,'套管反向圆转换引用'),parameters=external.circleParameters;
        if(!sourceFeature.parents.includes('凸台-拉伸2')||!near(parameters.slice(0,3),sourceCaps.start)||Math.abs(parameters[6]-sourceCaps.circle.geometry.radius)>1e-8||!near(profile.sketch.segments[0].geometry.center,[0,0,0]))throw new DefinitionError('套管反向圆未对应原圆柱起始边');
        const sourceCircle=one(sourceFeature.sketch.segments.filter(segment=>!segment.construction),'套管反向原圆'),targetCircle=one(target.sketch.segments.filter(segment=>!segment.construction),'套管反向圆'),center=transformPoint(targetCaps.start,swMatrix(target.sketch.modelToSketch,1)),radius=targetCaps.circle.geometry.radius,phase=sourceCircle.geometry.start.map((value,axis)=>(value-sourceCircle.geometry.center[axis])/sourceCircle.geometry.radius);
        targetCircle.geometry.center=center;targetCircle.geometry.radius=radius;targetCircle.geometry.start=center.map((value,axis)=>value+phase[axis]*radius);targetCircle.geometry.end=[...targetCircle.geometry.start];for(const point of target.sketch.points)if(near(point.coordinates,sourceCircle.geometry.center))point.coordinates=[...center];
        evidence.push({feature:sourceFeature.name,source:'native-converted-circle-at-cylinder-start',radiusMm:radius*1000});
      }else{
        const local=Object.fromEntries(Object.entries(dimensions).filter(([name])=>name.split('@').at(-1)===sourceFeature.name)),drive=axisSketchDrive(original,sourceFeature.name,{governingDimensions:Object.keys(local)}),parents=topologies.filter(topology=>sourceFeature.parents.includes(topology.feature)),support=parents.length?mapExtrudedPlane(sketchSupportPlane(sourceFeature.sketch),parents):{delta:[0,0,0],proof:[]},matrix=movedSketchMatrix(sourceFeature.sketch,support.delta),references=Object.fromEntries(drive.references.map(reference=>[reference.id,mapSketchReference(reference,sourceFeature.sketch,matrix,parents,sourceFeature.parents.includes('原点'))])),solved=drive.solve(local,references);
        target.sketch=solved.sketch;target.sketch.modelToSketch=matrix;evidence.push({feature:sourceFeature.name,dimensions:local,support:support.proof,source:'native-profile-and-external-edge-constraints'});
      }
    }
    if(['Extrusion','Boss'].includes(sourceFeature.type))topologies.push(['凸台-拉伸2','凸台-拉伸3'].includes(sourceFeature.name)?pairCircularCaps(original,configuration,sourceFeature.name):pairedExtrudedTopology(original,configuration,sourceFeature.name));
    if(sourceFeature.type==='MirrorSolid'){
      const oldInside=circularCaps(original,'凸台-拉伸2'),oldOutside=circularCaps(original,'凸台-拉伸3'),newInside=circularCaps(configuration,'凸台-拉伸2'),newOutside=circularCaps(configuration,'凸台-拉伸3'),oldMiddle=oldInside.end.map((value,axis)=>(value+oldOutside.end[axis])/2),newMiddle=newInside.end.map((value,axis)=>(value+newOutside.end[axis])/2),reference=sourceFeature.definition.properties.Face,transform=reference?.referencePlaneTransform;
      if(!transform||!near(transform.slice(9,12),oldMiddle))throw new DefinitionError('套管原镜像面未处于两侧圆柱端面中间');for(let axis=0;axis<3;axis++)target.definition.properties.Face.referencePlaneTransform[9+axis]+=newMiddle[axis]-oldMiddle[axis];evidence.push({feature:sourceFeature.name,source:'native-midplane-between-cylinder-ends',originMm:newMiddle.map(value=>value*1000)});
    }
    if(sourceFeature.type==='LPattern'){
      const properties=sourceFeature.definition.properties,updated=target.definition.properties,spacing=Object.entries(dimensions).filter(([name])=>name.split('@').at(-1)===sourceFeature.name);
      if(spacing.length!==1||properties.D1TotalInstances!==2||properties.D2TotalInstances!==1||properties.instanceTransforms.length!==2||!(properties.D1Spacing>0)||!(spacing[0][1]>0))throw new DefinitionError('套管原桩头阵列不是双种子间距驱动');
      const ratio=spacing[0][1]/1000/properties.D1Spacing;updated.D1Spacing=spacing[0][1]/1000;for(const transform of updated.instanceTransforms)for(let axis=0;axis<3;axis++)transform[9+axis]*=ratio;evidence.push({feature:sourceFeature.name,source:'native-body-seeds-and-spacing',spacingMm:spacing[0][1]});
    }
  }
  for(const equation of configuration.equations)if(equation.globalVariable){const name=/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1];if(Object.hasOwn(model.globalVariables,name))equation.expression='"'+name+'" = '+model.globalVariables[name];}
  return {template,topologies,evidence,source:'captured-wall-bushing-profiles-holes-and-body-patterns',nativeModelValidationRequired:false};
}

// Exact driven cut sketch, mirror datum and native body-pattern transforms.
// No hole is reconstructed from a mesh box or from the terminal's position.
export function wallBushingHoleSources(derived,model,placement,instanceId){
  const configuration=one(derived.template.configurations.filter(row=>row.name===model.configuration),'套管孔源配置'),
    cut=one(configuration.features.filter(row=>row.type==='Cut'&&!row.suppressed),'套管桩头原开孔'),
    profile=one(configuration.features.filter(row=>row.sketch&&cut.parents.includes(row.name)),'套管开孔草图'),
    mirror=one(configuration.features.filter(row=>row.type==='MirrorSolid'&&!row.suppressed),'套管桩头镜向'),
    basis=inverseRigidMatrix(swMatrix(profile.sketch.modelToSketch)),plane=swMatrix(mirror.definition.properties.Face.referencePlaneTransform),
    planeNormal=plane.slice(8,11),planeOrigin=plane.slice(12,15);
  if(cut.definition.properties.forwardEndCondition!==1)throw new DefinitionError('套管孔源不是原贯穿切除');
  const dot=(a,b)=>a.reduce((sum,value,index)=>sum+value*b[index],0),subtract=(a,b)=>a.map((value,index)=>value-b[index]),
    reflectVector=value=>value.map((entry,index)=>entry-2*dot(value,planeNormal)*planeNormal[index]),
    reflectPoint=value=>value.map((entry,index)=>entry-2*dot(subtract(value,planeOrigin),planeNormal)*planeNormal[index]),
    direction=(vector,matrix)=>subtract(transformPoint(vector,matrix),transformPoint([0,0,0],matrix)),seeds=[];
  for(const segment of profile.sketch.segments.filter(row=>!row.construction)){
    if(segment.type!==1||!near(segment.geometry.start,segment.geometry.end))throw new DefinitionError('套管孔源包含未支持的非整圆');
    const normal=basis.slice(8,11),faceCenter=transformPoint(segment.geometry.center.map(value=>value*1000),basis),thickness=model.globalVariables.C8,
      center=faceCenter.map((value,index)=>value+normal[index]*thickness/2),axis=basis.slice(0,3),diameter=segment.geometry.radius*2000;
    if(!segment.id||!(thickness>0)||instanceId==null)throw new DefinitionError('套管孔源缺少原孔身份、桩头厚度或安装实例身份');
    for(const reflected of [false,true])seeds.push({id:cut.id+':'+segment.id+':'+reflected,center:reflected?reflectPoint(center):center,
      normal:reflected?reflectVector(normal):normal,axis:reflected?reflectVector(axis):axis,diameter,thickness});
  }
  const holes=[...seeds];
  for(const pattern of configuration.features.filter(row=>row.type==='LPattern'&&!row.suppressed)){
    const properties=pattern.definition.properties;
    if(!properties.BodyPattern||properties.D2TotalInstances!==1||properties.instanceTransforms.length!==properties.D1TotalInstances)
      throw new DefinitionError('套管孔源阵列未对应原单向实体阵列');
    if(properties.PatternBodyArray.length!==2||![cut.id,mirror.id].every(id=>properties.PatternBodyArray.some(body=>body.ownershipEvidence?.faces.some(face=>face.oldestOwner?.id===id))))
      throw new DefinitionError('套管孔源阵列未引用原带孔桩头实体');
    for(const [index,values] of properties.instanceTransforms.entries()){
      if(index===0)continue;
      const transform=swMatrix(values);
      for(const source of seeds)holes.push({...source,id:source.id+':'+pattern.id+':'+index,center:transformPoint(source.center,transform),normal:direction(source.normal,transform),axis:direction(source.axis,transform)});
    }
  }
  return holes.map(hole=>({...hole,id:instanceId+':'+model.modelId+':'+hole.id,center:transformPoint(hole.center,placement).map(Math.fround),
    normal:direction(hole.normal,placement).map(Math.fround),axis:direction(hole.axis,placement).map(Math.fround),diameter:Math.fround(hole.diameter)}));
}

export function wallBushingDimensionParameters(derived,model,placement){
  const configuration=derived.template.configurations.find(configuration=>configuration.name===model.configuration),profile=configuration.features.find(feature=>feature.name==='草图5'),basis=inverseRigidMatrix(swMatrix(profile.sketch.modelToSketch,1)),edges=derived.topologies.find(topology=>topology.feature==='凸台-拉伸4').edges.map(pair=>pair.target),parameters=[];
  for(const [field,label,axis]of [['C1','桩头长度(C1)',2],['C2','桩头宽度(C2)',0],['C8','桩头厚度(C8)',1]]){
    const direction=basis.slice(axis*4,axis*4+3),value=model.globalVariables[field],edge=edges.find(edge=>{const vector=edge.end.map((coordinate,index)=>coordinate-edge.start[index]),extent=Math.hypot(...vector);return Math.abs(extent*1000-value)<1e-5&&Math.abs(Math.abs(vector.reduce((sum,coordinate,index)=>sum+coordinate*direction[index],0))-extent)<1e-8;});if(!edge)throw new DefinitionError('套管业务尺寸没有原轮廓见证边：'+field);
    parameters.push({name:'General.WallBushingTerminal.'+field,label,value,editable:true,annotationEdges:[[transformPoint(edge.start.map(value=>value*1000),placement),transformPoint(edge.end.map(value=>value*1000),placement)]],source:'native-terminal-edge-bound-to-original-ui-group'});
  }
  const circleProfile=configuration.features.find(feature=>feature.name==='草图2'),circle=circleProfile.sketch.segments.find(segment=>!segment.construction).geometry,toModel=inverseRigidMatrix(swMatrix(circleProfile.sketch.modelToSketch)),ends=[-1,1].map(sign=>transformPoint(circle.center.map((value,axis)=>(value+(axis===0?sign*circle.radius:0))*1000),toModel));
  parameters.push({name:'General.WallBushingTerminal.Diameter',label:'穿墙套管直径',value:model.globalVariables['φ'],editable:true,annotationEdges:[ends.map(point=>transformPoint(point,placement))],source:'native-cylinder-diameter-bound-to-original-ui-group'});return parameters;
}
