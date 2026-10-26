locals {
  contract = yamldecode(file("${path.module}/../../../contracts/nats.contract.yaml"))

  base_subject = local.contract.base_subject

  streams = merge([
    for service_name, service in local.contract.services : {
      for stream_type, stream in service.streams :
      stream.name => {
        name      = stream.name
        retention = stream.retention
        subjects = [
          for _, subject in stream.subjects : "${local.base_subject}.${subject}"
        ]
      }
    }
  ]...)

  consumers = merge([
    for service_name, service in local.contract.services : merge([
      for stream_type, stream in service.streams : {
        for consumer in try(stream.consumers, []) :
        consumer.durable_name => {
          stream_name  = stream.name
          durable_name = consumer.durable_name
          filter_subjects = [
            for subject in try(consumer.filter_subjects, []) : "${local.base_subject}.${subject}"
          ]
        }
      }
    ]...)
  ]...)
}
