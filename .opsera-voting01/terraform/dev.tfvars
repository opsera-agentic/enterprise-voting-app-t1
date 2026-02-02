app_name    = "voting01"
environment = "dev"
aws_region  = "us-west-2"

# VPC and Networking (from opsera-usw2-np cluster)
vpc_id             = "vpc-043634dd380b12814"
private_subnet_ids = ["subnet-079509530912d0d93", "subnet-0f4f896e29dca93d6"]

# IRSA OIDC Provider
eks_oidc_provider_arn = "arn:aws:iam::792373136340:oidc-provider/oidc.eks.us-west-2.amazonaws.com/id/A3D4B9F06FD499E4752FF4485264AA49"
eks_cluster_name      = "opsera-usw2-np"

# Database sizing (dev - minimal)
rds_instance_class    = "db.t3.micro"
rds_allocated_storage = 20
redis_node_type       = "cache.t3.micro"
