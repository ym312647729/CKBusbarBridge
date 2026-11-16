import {DefinitionError} from './expressions.mjs';
import {identityMatrix,reflectionMatrix} from './feature-patterns.mjs';

// Bodies keep their operation ancestry. Native face ownership selects an existing body uniquely.
export class NativeBodyTracker{
  constructor(configuration){this.configuration=configuration;this.bodies=[];this.visited=new Set();this.selections=[];}
  ownerId(owner){
    if(!owner?.id||!owner.name||owner.readError)throw new DefinitionError('原生实体面的所属特征证据不完整');
    const matches=this.configuration.features.filter(feature=>feature.id===owner.id&&feature.name===owner.name);
    if(matches.length!==1||!this.visited.has(owner.id))throw new DefinitionError('实体归属特征尚未唯一生成：'+owner.name);
    return owner.id;
  }
  select(reference,feature){
    const faces=reference?.ownershipEvidence?.faces;
    if(reference?.kind!=='source-solid-body-selection'||!faces?.length||faces.some(face=>!face.id))throw new DefinitionError('所选实体缺少原始面所属特征：'+feature.name);
    const owners=new Set(faces.map(face=>this.ownerId(face.oldestOwner))),seeds=new Set();
    for(const face of faces)for(const owner of [face.patternSeed,face.bodySeed])if(owner)seeds.add(this.ownerId(owner));
    const candidates=this.bodies.filter(body=>[...owners].every(id=>body.owners.has(id))&&[...seeds].every(id=>body.seeds.has(id)));
    if(candidates.length!==1)throw new DefinitionError('实体归属证据不能唯一对应已生成实体：'+feature.name+'，候选数='+candidates.length);
    this.selections.push({feature:feature.name,selectionId:reference.id,ownerFeatureIds:[...owners],seedFeatureIds:[...seeds],matchingBodies:1});
    return candidates[0];
  }
  targets(feature){
    const properties=feature.definition.properties,references=properties.FeatureScopeBodies;
    if(properties.AutoSelect===false&&references?.length){const selected=references.map(reference=>this.select(reference,feature));if(new Set(selected).size!==selected.length)throw new DefinitionError('作用实体重复选择：'+feature.name);return selected;}
    if(this.bodies.length===1)return [this.bodies[0]];
    throw new DefinitionError('多实体作用范围未唯一指定：'+feature.name);
  }
  insert(solid,owners,seeds=new Set()){
    const pieces=solid.decompose();
    try{for(const piece of pieces){if(piece.status()!=='NoError')throw new DefinitionError('实体拓扑无效');this.bodies.push({solid:piece,owners:new Set(owners),seeds:new Set(seeds)});}}
    catch(error){for(const piece of pieces)if(!this.bodies.some(body=>body.solid===piece))piece.delete();throw error;}
  }
  apply(feature,operations){
    const properties=feature.definition.properties;
    if(feature.type==='MirrorSolid'||feature.type==='LPattern'&&properties.BodyPattern){
      for(const operation of operations){if(operation.kind!=='add'||!operation.nativeBodySeeds)throw new DefinitionError('实体复制缺少已核验的原始种子');this.insert(operation.solid,new Set([feature.id]),new Set(operation.nativeBodySeeds));}
    }else if(['Extrusion','Boss'].includes(feature.type)&&operations.every(operation=>operation.kind==='add')){
      if(!this.bodies.length||properties.Merge===false){for(const operation of operations)this.insert(operation.solid,new Set([feature.id]));}
      else{
        if(properties.Merge!==true)throw new DefinitionError('多实体合并模式未知：'+feature.name);
        const targets=this.targets(feature),owners=new Set([feature.id,...targets.flatMap(body=>[...body.owners])]);let combined=targets[0].solid.transform(identityMatrix);
        try{for(const operand of [...targets.slice(1).map(body=>body.solid),...operations.map(operation=>operation.solid)]){const next=combined.add(operand);combined.delete();combined=next;}this.insert(combined,owners);}finally{combined.delete();}
        for(const body of targets)body.solid.delete();this.bodies=this.bodies.filter(body=>!targets.includes(body));
      }
    }else if(feature.type==='Cut'&&operations.every(operation=>operation.kind==='subtract')){
      const targets=this.targets(feature);
      for(const body of targets){let cut=body.solid.transform(identityMatrix);try{for(const operation of operations){const next=cut.subtract(operation.solid);cut.delete();cut=next;}this.insert(cut,new Set([...body.owners,feature.id]),body.seeds);}finally{cut.delete();}}
      for(const body of targets)body.solid.delete();
      this.bodies=this.bodies.filter(body=>!targets.includes(body));
    }else throw new DefinitionError('该特征的多实体作用范围尚未支持：'+feature.name);
    this.visited.add(feature.id);
  }
  patternOperations(feature,convertTransform){
    const properties=feature.definition.properties;
    if(properties.VarySketch||properties.InstancesToVary||properties.PatternFaceArray?.length||properties.MirrorFaceArray?.length)throw new DefinitionError('变化实例或面阵列尚未支持：'+feature.name);
    if(!properties.PatternBodyArray?.length)throw new DefinitionError('实体复制没有原始实体选择：'+feature.name);
    const seeds=properties.PatternBodyArray.map(reference=>this.select(reference,feature));
    if(new Set(seeds).size!==seeds.length)throw new DefinitionError('实体复制种子身份重复：'+feature.name);
    let matrices;
    if(feature.type==='MirrorSolid'){
      if(properties.Merge!==false||properties.KnitSurface)throw new DefinitionError('合并或曲面实体镜像尚未支持：'+feature.name);
      matrices=[reflectionMatrix(properties.Face)];
    }else{
      const count=properties.D1TotalInstances*Math.max(1,properties.D2TotalInstances),transforms=properties.instanceTransforms;
      if(!properties.BodyPattern||transforms?.length!==count||!transforms.every(Array.isArray))throw new DefinitionError('实体阵列实例变换不完整：'+feature.name);
      const skipped=new Set(properties.SkippedItemArray??[]);matrices=transforms.flatMap((transform,index)=>index===0||skipped.has(index)?[]:[convertTransform(transform)]);
    }
    const operations=[];
    try{for(const matrix of matrices)for(const seed of seeds)operations.push({kind:'add',solid:seed.solid.transform(matrix),nativeBodySeeds:[...(seed.seeds.size?seed.seeds:seed.owners)]});return operations;}
    catch(error){operations.forEach(operation=>operation.solid.delete());throw error;}
  }
  combined(){
    if(!this.bodies.length)throw new DefinitionError('实体历史没有有效材料');
    let combined=this.bodies[0].solid.transform(identityMatrix);
    try{for(const body of this.bodies.slice(1)){const next=combined.add(body.solid);combined.delete();combined=next;}return combined;}catch(error){combined.delete();throw error;}
  }
  exportSolids(){const exported=[];try{for(const body of this.bodies)exported.push(body.solid.transform(identityMatrix));return exported;}catch(error){exported.forEach(solid=>solid.delete());throw error;}}
  dispose(){this.bodies.forEach(body=>body.solid.delete());this.bodies=[];}
}
